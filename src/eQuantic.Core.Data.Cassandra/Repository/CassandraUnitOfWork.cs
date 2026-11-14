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
///     tracking; the lightweight transactions among them (a concurrency token's writes) run one at a time per partition,
///     pinned to the session's serial consistency (<see cref="CassandraLightweightTransactions" />). An explicit
///     transaction defers its writes and runs them as one atomic <c>LOGGED BATCH</c> on <see cref="CommitTransactionAsync" />.
/// </summary>
public abstract partial class CassandraUnitOfWork : IQueryableUnitOfWork
{
    protected readonly IServiceProvider ServiceProvider;
    protected readonly ISession Session;
    protected readonly CassandraModel Model;

    private readonly List<PendingStatement> _pending = [];
    private bool _inTransaction;
    private bool _disposed;

    protected CassandraUnitOfWork(IServiceProvider serviceProvider, ISession session, CassandraModel model)
    {
        ServiceProvider = serviceProvider;
        Session = session;
        Model = model;
    }

    internal CassandraEntityConfiguration Configuration<TEntity>() => Model.For(typeof(TEntity));

    internal CassandraEntityConfiguration? ConfigurationOf(Type entityType) =>
        Model.Configurations.TryGetValue(entityType, out var configuration) ? configuration : null;

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

    /// <summary>
    ///     Stages an insert (<c>Add</c>): an upsert, which for an entity with store-generated columns is the insert of a
    ///     new row with what the store assigns (a sequence's next number, <c>toTimestamp(now())</c>), read back by the
    ///     row's key when the commit runs.
    /// </summary>
    internal void StageInsert<TEntity>(TEntity item) where TEntity : class
    {
        var configuration = Configuration<TEntity>();

        // A token entity's write is decided by its version (a new row at the default): the upsert decides it.
        if (configuration.StoreGenerated.Count == 0 || configuration.ConcurrencyColumn is not null)
        {
            StageUpsert(item);
            return;
        }

        GuardCounters(typeof(TEntity), configuration);
        EntityLifecycle.StampForInsert(item, Conventions, ServiceProvider);
        EntityLifecycle.StampForUpdate(item, Conventions, ServiceProvider);
        _pending.Add(new PendingStatement(() => CassandraMapper.BuildInsert(configuration, item), false)
        {
            Generating = item,
            Configuration = configuration,
        });
    }

    /// <summary>
    ///     Inserts each item only if no row with its primary key exists (<c>INSERT … IF NOT EXISTS</c>), at once, and answers
    ///     which applied, in order. Every number the items' sequences hand out is reserved first, in one compare-and-set per
    ///     sequence (an item that does not apply leaves its number unused: a gap, never a repeat). The inserts are lightweight
    ///     transactions, run one at a time per partition; an applied one's clock columns are read back at the serial
    ///     consistency it ran at. A transaction that times out under contention is settled by a serial read of its row: the
    ///     row the item wrote means it applied, another row means the key was taken, and no row means it is tried again.
    /// </summary>
    internal async Task<bool[]> InsertIfNotExistsAsync<TEntity>(IReadOnlyList<TEntity> items) where TEntity : class
    {
        var configuration = Configuration<TEntity>();
        GuardCounters(typeof(TEntity), configuration);
        var inserts = items.Select(item => (Item: item, Statement: new PendingStatement(() =>
        {
            var (cql, values) = CassandraMapper.BuildInsert(configuration, item);
            return (cql + " IF NOT EXISTS", values);
        }, true)
        {
            Generating = configuration.StoreGenerated.Count > 0 ? item : null,
            Configuration = configuration,
            Partition = CassandraMapper.PartitionText(configuration, item),
        })).ToList();

        await AssignSequencesAsync(inserts.Select(insert => insert.Statement).ToList()).ConfigureAwait(false);
        return await CassandraLightweightTransactions.PerPartitionAsync(inserts, insert => insert.Statement.Partition!, async insert =>
        {
            for (var attempt = 1; ; attempt++)
            {
                var (cql, values) = insert.Statement.Build();
                try
                {
                    var rows = await CassandraStatements.ExecuteConditionalAsync(Session, cql, values, null, CommandLogger, SensitiveLogging)
                        .ConfigureAwait(false);
                    var applied = rows.First().GetValue<bool>("[applied]");
                    if (applied)
                    {
                        await ReadBackAsync(insert.Statement, null).ConfigureAwait(false);
                    }

                    return applied;
                }
                catch (Exception exception) when (CassandraLightweightTransactions.IsTimedOut(exception) && attempt < SettleAttempts)
                {
                    // Its outcome unknown: the serial read settles it. The row this insert wrote is the item's (its own
                    // values), and then it applied; another row took the key; no row means it did not apply, and it runs again.
                    var (read, keyValues) = CassandraMapper.BuildRead(configuration, insert.Item);
                    var row = (await CassandraStatements.ExecuteSerialAsync(Session, read, keyValues, CommandLogger, SensitiveLogging)
                        .ConfigureAwait(false)).FirstOrDefault();
                    if (row is not null)
                    {
                        if (!CassandraMapper.HoldsInsert(configuration, CassandraMapper.Materialize<TEntity>(configuration, row), insert.Item))
                        {
                            return false;
                        }

                        CassandraMapper.AssignReadBack(configuration, insert.Item, row);
                        return true;
                    }

                    await CassandraLightweightTransactions.BackoffAsync(attempt).ConfigureAwait(false);
                }
            }
        }).ConfigureAwait(false);
    }

    private const int SettleAttempts = 50;

    /// <summary>Refuses an insert into a counter table: Cassandra writes a counter only by increments, never by an insert.</summary>
    /// <param name="entityType">The entity.</param>
    /// <param name="configuration">Its mapping.</param>
    internal static void GuardCounters(Type entityType, CassandraEntityConfiguration configuration)
    {
        if (configuration.CounterColumns.Count > 0)
        {
            throw new NotSupportedException(
                $"'{entityType.Name}' maps counter columns; a counter table has no inserts — mutate it through " +
                "UpdateMany increments (x => new ... { N = x.N + n }).");
        }
    }

    internal void StageUpsert<TEntity>(TEntity item) where TEntity : class
    {
        var configuration = Configuration<TEntity>();
        GuardCounters(typeof(TEntity), configuration);

        // Cassandra writes are upserts, so both stamps apply: CreatedAt only when unset, UpdatedAt always.
        EntityLifecycle.StampForInsert(item, Conventions, ServiceProvider);
        EntityLifecycle.StampForUpdate(item, Conventions, ServiceProvider);

        if (configuration.ConcurrencyColumn is not null)
        {
            GuardConditionalInTransaction<TEntity>();

            // A version at its default is a new row: with store-generated columns it is an insert the store fills.
            var inserts = configuration.StoreGenerated.Count > 0
                          && Convert.ToInt64(typeof(TEntity).GetProperty(configuration.MemberFor(configuration.ConcurrencyColumn),
                              System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase)
                              ?.GetValue(item) ?? 0L) == 0;
            if (inserts)
            {
                _pending.Add(new PendingStatement(() => CassandraMapper.BuildConditionalUpsert(configuration, item), true)
                {
                    Generating = item,
                    Configuration = configuration,
                    Partition = CassandraMapper.PartitionText(configuration, item),
                });
                return;
            }

            var conditional = CassandraMapper.BuildConditionalUpsert(configuration, item);
            _pending.Add(new PendingStatement(conditional.Cql, conditional.Values, true)
            {
                Partition = CassandraMapper.PartitionText(configuration, item),
            });
            return;
        }

        var statement = CassandraMapper.BuildUpsert(configuration, item);
        _pending.Add(new PendingStatement(statement.Cql, statement.Values, false));
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
            _pending.Add(new PendingStatement(conditional.Cql, conditional.Values, conditional.Cql.Contains(" IF ", StringComparison.Ordinal))
            {
                Partition = CassandraMapper.PartitionText(configuration, item),
            });
            return;
        }

        var statement = CassandraMapper.BuildDelete(configuration, item);
        _pending.Add(new PendingStatement(statement.Cql, statement.Values, false));
    }

    /// <summary>
    ///     A LOGGED BATCH cannot carry a lightweight transaction alongside other partitions' writes — Cassandra
    ///     restricts conditional batches to a single partition. The combination refuses instead of degrading.
    /// </summary>
    private void GuardConditionalInTransaction<TEntity>(CassandraEntityConfiguration configuration)
    {
        if (configuration.ConcurrencyColumn is not null)
        {
            GuardConditionalInTransaction<TEntity>();
        }
    }

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

        // The sequences hand out their numbers before any statement is built: the inserts carry them.
        await AssignSequencesAsync(statements).ConfigureAwait(false);

        // Each distinct CQL text (one per entity shape) is prepared once per session and bound per write; a TTL
        // applies to this flush's inserts only (deletes carry none). A conditional (LWT) write answers whether it
        // applied — an unapplied one means another writer won since the entity was read. The plain writes run all at
        // once; the conditional ones one at a time per partition, so no two of them contend in one partition's Paxos.
        async Task<int> ExecuteAsync(PendingStatement statement)
        {
            var (cql, values) = WithTtl(statement.Build(), ttlSeconds);
            if (!statement.Conditional)
            {
                await CassandraStatements.ExecuteAsync(Session, cql, values, consistency, CommandLogger, SensitiveLogging).ConfigureAwait(false);
                return 1;
            }

            var rows = await CassandraStatements.ExecuteConditionalAsync(Session, cql, values, consistency, CommandLogger, SensitiveLogging)
                .ConfigureAwait(false);
            return rows.FirstOrDefault() is { } row && !row.GetValue<bool>("[applied]") ? 0 : 1;
        }

        var plain = Task.WhenAll(statements.Where(statement => !statement.Conditional).Select(ExecuteAsync));
        var conditional = CassandraLightweightTransactions.PerPartitionAsync(statements.Where(statement => statement.Conditional).ToList(),
            statement => statement.Partition ?? string.Empty, ExecuteAsync);
        var applied = (await plain.ConfigureAwait(false)).Sum() + (await conditional.ConfigureAwait(false)).Sum();
        if (applied != statements.Count)
        {
            throw new ConcurrencyConflictException(statements.Count, applied);
        }

        await ReadBackAsync(statements, consistency).ConfigureAwait(false);
        return statements.Count;
    }

    /// <summary>Gives these entities, about to be inserted, the numbers their sequences hand out.</summary>
    internal Task AssignSequencesAsync(CassandraEntityConfiguration configuration, IEnumerable<object> entities) =>
        AssignSequencesAsync(entities.Select(entity => new PendingStatement(() => default, false)
        {
            Generating = entity,
            Configuration = configuration,
        }).ToList());

    /// <summary>
    ///     Stages an update of the named members only (and of the update's lifecycle stamps): <c>UPDATE … SET</c> those
    ///     columns <c>WHERE</c> the primary key — a lightweight transaction on the version read when the entity carries a
    ///     token. Every other column keeps what it holds. Cassandra's <c>UPDATE</c> is an upsert: a row deleted meanwhile
    ///     is written again, with these columns only.
    /// </summary>
    internal void StagePartialUpdate<TEntity>(TEntity item, IReadOnlyList<string> members) where TEntity : class
    {
        var configuration = Configuration<TEntity>();
        var columns = new List<CassandraColumn>();
        foreach (var member in members)
        {
            var column = configuration.Column(configuration.ColumnFor(member))
                         ?? throw new NotSupportedException($"'{typeof(TEntity).Name}' has no mapped member '{member}' to modify.");
            if (configuration.IsKey(column.Name))
            {
                throw new NotSupportedException($"'{member}' is part of the primary key, which identifies the row and is never modified.");
            }

            if (configuration.IsStoreGenerated(column.Name) || configuration.IsCounter(column.Name)
                || CassandraEntityConfiguration.Same(column.Name, configuration.ConcurrencyColumn ?? string.Empty))
            {
                throw new NotSupportedException(
                    $"'{member}' is store-generated, a counter or the concurrency token: an update does not set it.");
            }

            columns.Add(column);
        }

        GuardConditionalInTransaction<TEntity>(configuration);
        EntityLifecycle.StampForUpdate(item, Conventions, ServiceProvider);
        foreach (var stamp in EntityLifecycle.UpdateStamps(typeof(TEntity), Conventions, ServiceProvider))
        {
            if (configuration.Columns.FirstOrDefault(column => CassandraEntityConfiguration.Same(column.Member, stamp.Name)) is { } stampColumn
                && !columns.Contains(stampColumn))
            {
                columns.Add(stampColumn);
            }
        }

        var keys = CassandraMapper.PrimaryKey(configuration);
        var sets = columns.Select(column => $"{column.Name} = ?").ToList();
        var values = columns.Select(column => CassandraValues.ToStored(column, Read(item, column.Member))).ToList();
        values.AddRange(keys.Select(key => CassandraValues.ToStored(configuration.Column(key), Read(item, configuration.MemberFor(key)))));
        var cql = $"UPDATE {configuration.TableName} SET {string.Join(", ", sets)} WHERE {string.Join(" AND ", keys.Select(key => $"{key} = ?"))}";

        if (configuration.ConcurrencyColumn is not { } token)
        {
            _pending.Add(new PendingStatement(cql, values.ToArray(), false));
            return;
        }

        var property = typeof(TEntity).GetProperty(configuration.MemberFor(token),
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase)!;
        var current = Convert.ToInt64(property.GetValue(item) ?? 0L);
        property.SetValue(item, Convert.ChangeType(current + 1, Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType));
        values.Insert(sets.Count, current + 1);
        sets.Add($"{token} = ?");
        cql = $"UPDATE {configuration.TableName} SET {string.Join(", ", sets)} WHERE {string.Join(" AND ", keys.Select(key => $"{key} = ?"))} IF {token} = ?";
        values.Add(current);
        _pending.Add(new PendingStatement(cql, values.ToArray(), true) { Partition = CassandraMapper.PartitionText(configuration, item) });
    }

    private static object? Read(object entity, string member) =>
        entity.GetType().GetProperty(member, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                                             | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(entity);

    /// <summary>
    ///     Gives every staged insert the numbers its sequences hand out: one compare-and-set per sequence for the whole
    ///     commit, which reserves as many numbers as the commit inserts. It runs outside any batch (a lightweight
    ///     transaction cannot join a multi-partition batch): a batch that then fails leaves a gap, never a repeat.
    /// </summary>
    private async Task AssignSequencesAsync(IReadOnlyList<PendingStatement> statements)
    {
        foreach (var sequence in statements
                     .Where(statement => statement.Generating is not null)
                     .SelectMany(statement => statement.Configuration!.StoreGenerated
                         .Where(generated => generated.Value == Data.Modeling.StoreValue.Sequence)
                         .Select(generated => (statement.Generating!, statement.Configuration!, Generated: generated)))
                     .GroupBy(insert => CassandraSequences.SequenceName(insert.Item2.TableName, insert.Generated.Column)))
        {
            var count = sequence.Count();
            var last = await CassandraSequences.AdvanceAsync(Session, sequence.Key, count, CommandLogger, SensitiveLogging).ConfigureAwait(false);
            var next = last - count + 1;
            foreach (var (entity, _, generated) in sequence)
            {
                var property = entity.GetType().GetProperty(generated.Member,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase)!;
                property.SetValue(entity, Convert.ChangeType(next++,
                    Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType, System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>Reads each inserted row's clock columns back by its key, onto its entity.</summary>
    private Task ReadBackAsync(IReadOnlyList<PendingStatement> statements, ConsistencyLevel? consistency) =>
        Task.WhenAll(statements.Select(statement => ReadBackAsync(statement, consistency)));

    /// <summary>
    ///     Reads an inserted row's clock columns back by its key, onto its entity (nothing when it has none: a sequence's
    ///     number is on the entity already). A lightweight transaction's row is read at the serial consistency it ran at, so
    ///     the read sees what the transaction committed; a plain insert's at <paramref name="consistency" />.
    /// </summary>
    private async Task ReadBackAsync(PendingStatement statement, ConsistencyLevel? consistency)
    {
        if (statement.Generating is null || CassandraMapper.BuildReadBack(statement.Configuration!, statement.Generating) is not { } readBack)
        {
            return;
        }

        var (cql, values) = readBack;
        var rows = statement.Conditional
            ? await CassandraStatements.ExecuteSerialAsync(Session, cql, values, CommandLogger, SensitiveLogging).ConfigureAwait(false)
            : await CassandraStatements.ExecuteAsync(Session, cql, values, consistency, CommandLogger, SensitiveLogging).ConfigureAwait(false);
        CassandraMapper.AssignReadBack(statement.Configuration!, statement.Generating, rows.FirstOrDefault());
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

                await AssignSequencesAsync(writes).ConfigureAwait(false);

                var batch = new BatchStatement().SetBatchType(BatchType.Logged);
                foreach (var write in writes)
                {
                    var (cql, values) = write.Build();
                    batch.Add(await CassandraStatements.BindAsync(Session, cql, values).ConfigureAwait(false));
                }

                await Session.ExecuteAsync(batch).ConfigureAwait(false);
                await ReadBackAsync(writes, null).ConfigureAwait(false);
            }
        }
        finally
        {
            _inTransaction = false;
            await EndTransactionLocksAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Aborts the transaction and discards its deferred writes.</summary>
    public Task RollbackTransactionAsync(CancellationToken cancellationToken = default)
    {
        _pending.Clear();
        _inTransaction = false;
        return EndTransactionLocksAsync();
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

    protected virtual void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            ReleaseNamedLocks();
        }

        _disposed = _disposed || disposing;
    }
}

/// <summary>
///     A staged CQL write. Most are built when staged; an insert with store-generated columns is built at commit, once
///     its sequences have handed out their numbers, and its row is read back afterwards.
/// </summary>
internal sealed class PendingStatement
{
    public PendingStatement(string cql, object?[] values, bool conditional)
        : this(() => (cql, values), conditional)
    {
    }

    public PendingStatement(Func<(string Cql, object?[] Values)> build, bool conditional)
    {
        Build = build;
        Conditional = conditional;
    }

    /// <summary>Builds the statement (at commit for an insert the store fills).</summary>
    public Func<(string Cql, object?[] Values)> Build { get; }

    /// <summary>Whether it is a lightweight transaction that answers <c>[applied]</c>.</summary>
    public bool Conditional { get; }

    /// <summary>The entity whose row the store fills, when the statement inserts one with store-generated columns.</summary>
    public object? Generating { get; init; }

    /// <summary>That entity's mapping.</summary>
    public CassandraEntityConfiguration? Configuration { get; init; }

    /// <summary>
    ///     The partition a lightweight transaction writes, as text (<see cref="CassandraMapper.PartitionText(CassandraEntityConfiguration, object)" />): the commit
    ///     runs a partition's transactions one at a time.
    /// </summary>
    public string? Partition { get; init; }
}

/// <summary>The strongly-typed unit of work a consumer derives for its keyspace.</summary>
public abstract class CassandraUnitOfWork<TKeyspace>(IServiceProvider serviceProvider, ISession session, CassandraModel model)
    : CassandraUnitOfWork(serviceProvider, session, model)
    where TKeyspace : class;
