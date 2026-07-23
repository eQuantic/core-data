using System.Diagnostics;
using System.Linq.Expressions;
using eQuantic.Core.Data.Diagnostics;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using global::Cassandra;

namespace eQuantic.Core.Data.Cassandra.Repository;

/// <summary>
///     The native Apache Cassandra unit of work. Entity writes (a Cassandra <c>INSERT</c> is an upsert, so
///     <c>Add</c>/<c>Modify</c>/<c>Merge</c> all map to it, plus <c>Remove</c>) are buffered as CQL statements and
///     flushed on <see cref="CommitAsync(System.Threading.CancellationToken)" /> — executed concurrently, no change
///     tracking. An explicit transaction defers its writes and runs them as one atomic <c>LOGGED BATCH</c> on
///     <see cref="CommitTransactionAsync" />.
/// </summary>
public abstract class CassandraUnitOfWork : IQueryableUnitOfWork
{
    protected readonly IServiceProvider ServiceProvider;
    protected readonly ISession Session;
    protected readonly CassandraModel Model;

    private readonly List<(string Cql, object?[] Values, bool Conditional)> _pending = [];
    private bool _inTransaction;
    private bool _disposed;

    protected CassandraUnitOfWork(IServiceProvider serviceProvider, ISession session, CassandraModel model)
    {
        ServiceProvider = serviceProvider;
        Session = session;
        Model = model;
    }

    internal CassandraEntityConfiguration Configuration<TEntity>() => Model.For(typeof(TEntity));

    internal ISession GetSession() => Session;

    private QueryFilters? _queryFilters;
    private bool _queryFiltersResolved;
    private DataConventions? _conventions;

    /// <summary>The active write conventions — the registered <see cref="DataConventions" />, or the defaults.</summary>
    internal DataConventions Conventions =>
        _conventions ??= ServiceProvider.GetService(typeof(DataConventions)) as DataConventions ?? new DataConventions();

    /// <summary>The scope's service provider (handed to per-request convention accessors).</summary>
    internal IServiceProvider Services => ServiceProvider;

    private Microsoft.Extensions.Logging.ILogger? _commandLogger;

    /// <summary>The command-log category (<c>eQuantic.Core.Data.cassandra.Command</c>; null logger without DI logging).</summary>
    internal Microsoft.Extensions.Logging.ILogger CommandLogger =>
        _commandLogger ??= (ServiceProvider.GetService(typeof(Microsoft.Extensions.Logging.ILoggerFactory))
                as Microsoft.Extensions.Logging.ILoggerFactory)
            ?.CreateLogger("eQuantic.Core.Data.cassandra.Command")
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>Whether log events may carry parameter values (the explicit opt-in).</summary>
    internal bool SensitiveLogging => Conventions.EnableSensitiveDataLogging;

    /// <summary>The global filter registered for <typeparamref name="TEntity" /> in this scope, or <c>null</c>.</summary>
    /// <remarks>A soft-delete entity's live-rows filter is ANDed in by convention.</remarks>
    internal Expression<Func<TEntity, bool>>? GlobalFilter<TEntity>() where TEntity : class
    {
        if (!_queryFiltersResolved)
        {
            _queryFilters = ServiceProvider.GetService(typeof(QueryFilters)) as QueryFilters;
            _queryFiltersResolved = true;
        }

        return EntityLifecycle.And(
            _queryFilters?.FilterFor<TEntity>(ServiceProvider),
            EntityLifecycle.SoftDeleteFilter<TEntity>(Conventions));
    }

    // -------------------------------------------------------------- write staging

    internal void StageUpsert<TEntity>(TEntity item) where TEntity : class
    {
        var configuration = Configuration<TEntity>();
        if (configuration.CounterColumns.Count > 0)
        {
            throw new NotSupportedException(
                $"'{typeof(TEntity).Name}' maps counter columns; a counter table has no inserts — mutate it through " +
                "UpdateMany increments (x => new ... { N = x.N + n }).");
        }

        // Cassandra writes are upserts, so both stamps apply: CreatedAt only when unset, UpdatedAt always.
        EntityLifecycle.StampForInsert(item, Conventions, ServiceProvider);
        EntityLifecycle.StampForUpdate(item, Conventions, ServiceProvider);

        if (configuration.ConcurrencyColumn is not null)
        {
            GuardConditionalInTransaction<TEntity>();
            var conditional = CassandraMapper.BuildConditionalUpsert(configuration, item);
            _pending.Add((conditional.Cql, conditional.Values, true));
            return;
        }

        var statement = CassandraMapper.BuildUpsert(configuration, item);
        _pending.Add((statement.Cql, statement.Values, false));
    }

    internal void StageDelete<TEntity>(TEntity item) where TEntity : class
    {
        // A soft-delete entity's Remove stamps DeletedAt and stages an upsert — the row survives.
        if (EntityLifecycle.TrySoftDelete(item, Conventions, ServiceProvider))
        {
            StageUpsert(item);
            return;
        }

        var configuration = Configuration<TEntity>();
        if (configuration.ConcurrencyColumn is not null)
        {
            GuardConditionalInTransaction<TEntity>();
            var conditional = CassandraMapper.BuildConditionalDelete(configuration, item);
            _pending.Add((conditional.Cql, conditional.Values, conditional.Cql.Contains(" IF ", StringComparison.Ordinal)));
            return;
        }

        var statement = CassandraMapper.BuildDelete(configuration, item);
        _pending.Add((statement.Cql, statement.Values, false));
    }

    /// <summary>
    ///     A LOGGED BATCH cannot carry a lightweight transaction alongside other partitions' writes — Cassandra
    ///     restricts conditional batches to a single partition. The combination refuses instead of degrading.
    /// </summary>
    private void GuardConditionalInTransaction<TEntity>()
    {
        if (_inTransaction)
        {
            throw new NotSupportedException(
                $"'{typeof(TEntity).Name}' declares a concurrency token, and its conditional (LWT) write cannot run inside " +
                "an explicit transaction batch; commit it outside the transaction, or drop the token from the model.");
        }
    }

    // -------------------------------------------------------------- commit

    public int Commit() => CommitAsync().GetAwaiter().GetResult();

    public Task<int> CommitAsync(CancellationToken cancellationToken = default) => CommitCoreAsync(null, null, cancellationToken);

    public int Commit(Action<SaveOptions> options) => CommitAsync(options).GetAwaiter().GetResult();

    /// <summary>Commits with Cassandra save opt-ins: <c>o.WithConsistency(...)</c> and <c>o.WithTtl(...)</c>.</summary>
    public Task<int> CommitAsync(Action<SaveOptions> options, CancellationToken cancellationToken = default)
    {
        var saveOptions = GetSaveOptions();
        options?.Invoke(saveOptions);
        return CommitCoreAsync(
            CassandraSaveOptionsExtensions.ConsistencyOf(saveOptions),
            CassandraSaveOptionsExtensions.TtlOf(saveOptions),
            cancellationToken);
    }

    private async Task<int> CommitCoreAsync(ConsistencyLevel? consistency, int? ttlSeconds, CancellationToken cancellationToken)
    {
        if (_inTransaction)
        {
            return _pending.Count;
        }

        if (_pending.Count == 0)
        {
            return 0;
        }

        var statements = _pending.ToList();
        _pending.Clear();

        using var activity = DataActivitySource.Instance.StartActivity("cassandra.commit", ActivityKind.Client);
        activity?.SetTag("db.system", "cassandra");
        activity?.SetTag("equantic.writes", statements.Count);

        // Each distinct CQL text (one per entity shape) is prepared once per session and bound per write; a TTL
        // applies to this flush's inserts only (deletes carry none). A conditional (LWT) write answers whether it
        // applied — an unapplied one means another writer won since the entity was read.
        var results = await Task.WhenAll(statements.Select(async statement =>
        {
            var (cql, values) = WithTtl((statement.Cql, statement.Values), ttlSeconds);
            var rows = await CassandraStatements.ExecuteAsync(Session, cql, values, consistency, CommandLogger, SensitiveLogging).ConfigureAwait(false);
            return statement.Conditional && rows.FirstOrDefault() is { } row && !row.GetValue<bool>("[applied]") ? 0 : 1;
        })).ConfigureAwait(false);

        var applied = results.Sum();
        if (applied != statements.Count)
        {
            throw new ConcurrencyConflictException(statements.Count, applied);
        }

        return statements.Count;
    }

    private static (string Cql, object?[] Values) WithTtl((string Cql, object?[] Values) statement, int? ttlSeconds) =>
        ttlSeconds is { } ttl && statement.Cql.StartsWith("INSERT", StringComparison.Ordinal) && !statement.Cql.EndsWith("IF NOT EXISTS", StringComparison.Ordinal)
            ? (statement.Cql + " USING TTL ?", [.. statement.Values, ttl])
            : statement;
    public int CommitAndRefreshChanges() => Commit();
    public int CommitAndRefreshChanges(Action<SaveOptions> options) => Commit();
    public Task<int> CommitAndRefreshChangesAsync(CancellationToken cancellationToken = default) => CommitAsync(cancellationToken);
    public Task<int> CommitAndRefreshChangesAsync(Action<SaveOptions> options, CancellationToken cancellationToken = default) => CommitAsync(cancellationToken);

    /// <summary>Discards every staged (uncommitted) write.</summary>
    public void RollbackChanges() => _pending.Clear();

    public virtual SaveOptions GetSaveOptions() => new();

    // -------------------------------------------------------------- explicit transactions (LOGGED BATCH)

    /// <summary>Begins a transaction: subsequent writes are deferred and run atomically on <see cref="CommitTransactionAsync" />.</summary>
    public Task BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        _inTransaction = true;
        return Task.CompletedTask;
    }

    /// <summary>Runs the deferred writes as one atomic <c>LOGGED BATCH</c>.</summary>
    public async Task CommitTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (!_inTransaction)
        {
            return;
        }

        try
        {
            if (_pending.Count > 0)
            {
                var writes = _pending.ToList();
                _pending.Clear();

                using var activity = DataActivitySource.Instance.StartActivity("cassandra.commit_transaction", ActivityKind.Client);
                activity?.SetTag("db.system", "cassandra");
                activity?.SetTag("equantic.writes", writes.Count);

                var batch = new BatchStatement().SetBatchType(BatchType.Logged);
                foreach (var (cql, values, _) in writes)
                {
                    batch.Add(await CassandraStatements.BindAsync(Session, cql, values).ConfigureAwait(false));
                }

                await Session.ExecuteAsync(batch).ConfigureAwait(false);
            }
        }
        finally
        {
            _inTransaction = false;
        }
    }

    /// <summary>Aborts the transaction and discards its deferred writes.</summary>
    public Task RollbackTransactionAsync(CancellationToken cancellationToken = default)
    {
        _pending.Clear();
        _inTransaction = false;
        return Task.CompletedTask;
    }

    // -------------------------------------------------------------- repositories / sets

    public virtual IRepository<TEntity, TKey> GetRepository<TEntity, TKey>() where TEntity : class, IEntity<TKey> =>
        (IRepository<TEntity, TKey>)ServiceProvider.GetService(typeof(IRepository<TEntity, TKey>))!;

    public IAsyncRepository<TEntity, TKey> GetAsyncRepository<TEntity, TKey>() where TEntity : class, IEntity<TKey> =>
        (IAsyncRepository<TEntity, TKey>)ServiceProvider.GetService(typeof(IAsyncRepository<TEntity, TKey>))!;

    public IQueryableRepository<TEntity, TKey> GetQueryableRepository<TEntity, TKey>() where TEntity : class, IEntity<TKey> =>
        (IQueryableRepository<TEntity, TKey>)ServiceProvider.GetService(typeof(IQueryableRepository<TEntity, TKey>))!;

    public IAsyncQueryableRepository<TEntity, TKey> GetAsyncQueryableRepository<TEntity, TKey>() where TEntity : class, IEntity<TKey> =>
        (IAsyncQueryableRepository<TEntity, TKey>)ServiceProvider.GetService(typeof(IAsyncQueryableRepository<TEntity, TKey>))!;

    public Data.Repository.ISet<TEntity> CreateSet<TEntity>() where TEntity : class, IEntity =>
        new CassandraSet<TEntity>(this, Session);

    // -------------------------------------------------------------- change-tracking hooks (mapped to the lean model)

    /// <summary>Stages the current entity as an upsert.</summary>
    public void ApplyCurrentValues<TEntity>(TEntity original, TEntity current) where TEntity : class, IEntity =>
        StageUpsert(current);

    /// <summary>No-op: the lean model does not track read entities.</summary>
    public void Attach<TEntity>(TEntity item) where TEntity : class, IEntity { }

    /// <summary>Stages the entity as an upsert.</summary>
    public void SetModified<TEntity>(TEntity item) where TEntity : class => StageUpsert(item);

    public void LoadCollection<TEntity, TElement>(TEntity item,
        System.Linq.Expressions.Expression<Func<TEntity, IEnumerable<TElement>>> navigationProperty,
        System.Linq.Expressions.Expression<Func<TElement, bool>>? filter = null)
        where TEntity : class where TElement : class =>
        throw new NotSupportedException("Cassandra rows are self-contained; model related data with the partition key or query it explicitly.");

    public Task LoadCollectionAsync<TEntity, TElement>(TEntity item,
        System.Linq.Expressions.Expression<Func<TEntity, IEnumerable<TElement>>> navigationProperty,
        System.Linq.Expressions.Expression<Func<TElement, bool>>? filter = null)
        where TEntity : class where TElement : class =>
        throw new NotSupportedException("Cassandra rows are self-contained; model related data with the partition key or query it explicitly.");

    public void Reload<TEntity>(TEntity item) where TEntity : class =>
        throw new NotSupportedException("The lean unit of work does not track read entities; re-query the row instead.");

    // -------------------------------------------------------------- dispose

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing) => _disposed = _disposed || disposing;
}

/// <summary>The strongly-typed unit of work a consumer derives for its keyspace.</summary>
public abstract class CassandraUnitOfWork<TKeyspace>(IServiceProvider serviceProvider, ISession session, CassandraModel model)
    : CassandraUnitOfWork(serviceProvider, session, model)
    where TKeyspace : class;
