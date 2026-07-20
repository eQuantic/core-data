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

    private readonly List<SimpleStatement> _pending = [];
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

    // -------------------------------------------------------------- write staging

    internal void StageUpsert<TEntity>(TEntity item) where TEntity : class
    {
        var (cql, values) = CassandraMapper.BuildUpsert(Configuration<TEntity>(), item);
        _pending.Add(new SimpleStatement(cql, values));
    }

    internal void StageDelete<TEntity>(TEntity item) where TEntity : class
    {
        var (cql, values) = CassandraMapper.BuildDelete(Configuration<TEntity>(), item);
        _pending.Add(new SimpleStatement(cql, values));
    }

    // -------------------------------------------------------------- commit

    public int Commit() => CommitAsync().GetAwaiter().GetResult();

    public async Task<int> CommitAsync(CancellationToken cancellationToken = default)
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

        await Task.WhenAll(statements.Select(statement => Session.ExecuteAsync(statement))).ConfigureAwait(false);
        return statements.Count;
    }

    public int Commit(Action<SaveOptions> options) => Commit();
    public Task<int> CommitAsync(Action<SaveOptions> options, CancellationToken cancellationToken = default) => CommitAsync(cancellationToken);
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
                var batch = new BatchStatement().SetBatchType(BatchType.Logged);
                foreach (var statement in _pending)
                {
                    batch.Add(statement);
                }

                _pending.Clear();
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
