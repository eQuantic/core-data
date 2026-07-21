using System.Diagnostics;
using System.Linq.Expressions;
using eQuantic.Core.Data.Diagnostics;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using Microsoft.Azure.Cosmos;

namespace eQuantic.Core.Data.CosmosDb.Repository;

/// <summary>
///     The native Azure Cosmos DB unit of work. Entity writes are staged as point operations and flushed on
///     <see cref="CommitAsync(System.Threading.CancellationToken)" /> — concurrently (bulk execution) so the
///     write amplifies to a single batched round trip, with no change tracking or snapshotting.
///     Because Cosmos transactions are limited to one container and one partition key, an explicit transaction
///     defers its staged writes and runs them as a single <see cref="TransactionalBatch" /> on
///     <see cref="CommitTransactionAsync" />.
/// </summary>
public abstract class CosmosUnitOfWork : IQueryableUnitOfWork
{
    protected readonly IServiceProvider ServiceProvider;
    protected readonly CosmosClient Client;
    protected readonly Database Database;
    protected readonly CosmosModel Model;

    private readonly List<CosmosPendingWrite> _pending = [];
    private bool _inTransaction;
    private bool _disposed;

    protected CosmosUnitOfWork(IServiceProvider serviceProvider, CosmosClient client, Database database, CosmosModel model)
    {
        ServiceProvider = serviceProvider;
        Client = client;
        Database = database;
        Model = model;
    }

    internal CosmosEntityConfiguration Configuration<TEntity>() => Model.For(typeof(TEntity));

    internal Container GetContainer<TEntity>() => Database.GetContainer(Configuration<TEntity>().ContainerName);

    private QueryFilters? _queryFilters;
    private bool _queryFiltersResolved;

    /// <summary>The global filter registered for <typeparamref name="TEntity" /> in this scope, or <c>null</c>.</summary>
    internal Expression<Func<TEntity, bool>>? GlobalFilter<TEntity>() where TEntity : class
    {
        if (!_queryFiltersResolved)
        {
            _queryFilters = ServiceProvider.GetService(typeof(QueryFilters)) as QueryFilters;
            _queryFiltersResolved = true;
        }

        return _queryFilters?.FilterFor<TEntity>(ServiceProvider);
    }

    // -------------------------------------------------------------- write staging (called by the repository/set)

    internal void StageInsert<TEntity>(TEntity item) where TEntity : class
    {
        var configuration = Configuration<TEntity>();
        var partitionKey = configuration.GetPartitionKey(item);
        _pending.Add(new CosmosPendingWrite(
            configuration.ContainerName,
            partitionKey,
            (container, cancellationToken) => container.CreateItemAsync(item, partitionKey, cancellationToken: cancellationToken),
            batch => batch.CreateItem(item)));
    }

    internal void StageUpsert<TEntity>(TEntity item) where TEntity : class
    {
        var configuration = Configuration<TEntity>();
        var partitionKey = configuration.GetPartitionKey(item);

        // An entity carrying its concurrency token stages a conditional replace: a concurrent change makes the
        // commit fail with a PreconditionFailed CosmosException instead of silently winning.
        if (configuration.GetETag(item) is { } etag)
        {
            var id = configuration.GetId(item);
            _pending.Add(new CosmosPendingWrite(
                configuration.ContainerName,
                partitionKey,
                (container, cancellationToken) => container.ReplaceItemAsync(item, id, partitionKey,
                    new ItemRequestOptions { IfMatchEtag = etag }, cancellationToken),
                batch => batch.ReplaceItem(id, item, new TransactionalBatchItemRequestOptions { IfMatchEtag = etag })));
            return;
        }

        _pending.Add(new CosmosPendingWrite(
            configuration.ContainerName,
            partitionKey,
            (container, cancellationToken) => container.UpsertItemAsync(item, partitionKey, cancellationToken: cancellationToken),
            batch => batch.UpsertItem(item)));
    }

    internal void StageDelete<TEntity>(TEntity item, string id) where TEntity : class
    {
        var configuration = Configuration<TEntity>();
        var partitionKey = configuration.GetPartitionKey(item);
        _pending.Add(new CosmosPendingWrite(
            configuration.ContainerName,
            partitionKey,
            (container, cancellationToken) => container.DeleteItemStreamAsync(id, partitionKey, cancellationToken: cancellationToken),
            batch => batch.DeleteItem(id)));
    }

    // -------------------------------------------------------------- commit (the single execution point)

    public int Commit() => CommitAsync().GetAwaiter().GetResult();

    public async Task<int> CommitAsync(CancellationToken cancellationToken = default)
    {
        // Inside a transaction the writes are deferred to CommitTransactionAsync, which runs them atomically.
        if (_inTransaction)
        {
            return _pending.Count;
        }

        if (_pending.Count == 0)
        {
            return 0;
        }

        var writes = _pending.ToList();
        _pending.Clear();

        using var activity = DataActivitySource.Instance.StartActivity("cosmosdb.commit", ActivityKind.Client);
        activity?.SetTag("db.system", "cosmosdb");
        activity?.SetTag("equantic.writes", writes.Count);

        await Task.WhenAll(writes.Select(write =>
            write.Execute(Database.GetContainer(write.ContainerName), cancellationToken))).ConfigureAwait(false);

        return writes.Count;
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

    // -------------------------------------------------------------- explicit transactions (single-partition batch)

    /// <summary>Begins a transaction: subsequent writes are deferred and run atomically on <see cref="CommitTransactionAsync" />.</summary>
    public Task BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        _inTransaction = true;
        return Task.CompletedTask;
    }

    /// <summary>Runs the deferred writes as one single-partition <see cref="TransactionalBatch" />.</summary>
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
                var partitions = _pending
                    .GroupBy(write => (write.ContainerName, write.PartitionKey.ToString()))
                    .ToList();
                if (partitions.Count > 1)
                {
                    throw new NotSupportedException(
                        "A Cosmos transaction is limited to a single container and partition key; commit cross-partition writes without a transaction.");
                }

                var writes = _pending.ToList();
                _pending.Clear();

                using var activity = DataActivitySource.Instance.StartActivity("cosmosdb.commit_transaction", ActivityKind.Client);
                activity?.SetTag("db.system", "cosmosdb");
                activity?.SetTag("equantic.writes", writes.Count);

                var batch = Database.GetContainer(writes[0].ContainerName).CreateTransactionalBatch(writes[0].PartitionKey);
                foreach (var write in writes)
                {
                    write.AddToBatch(batch);
                }

                using var response = await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"The Cosmos transactional batch failed with status {response.StatusCode}.");
                }
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
        new CosmosSet<TEntity>(this, GetContainer<TEntity>());

    // -------------------------------------------------------------- change-tracking hooks (mapped to the lean model)

    /// <summary>Stages the current entity as an upsert (the document store has no separate "apply values" step).</summary>
    public void ApplyCurrentValues<TEntity>(TEntity original, TEntity current) where TEntity : class, IEntity =>
        StageUpsert(current);

    /// <summary>No-op: the lean model does not track read entities, so there is nothing to attach.</summary>
    public void Attach<TEntity>(TEntity item) where TEntity : class, IEntity { }

    /// <summary>Stages the entity as an upsert.</summary>
    public void SetModified<TEntity>(TEntity item) where TEntity : class => StageUpsert(item);

    public void LoadCollection<TEntity, TElement>(TEntity item,
        System.Linq.Expressions.Expression<Func<TEntity, IEnumerable<TElement>>> navigationProperty,
        System.Linq.Expressions.Expression<Func<TElement, bool>>? filter = null)
        where TEntity : class where TElement : class =>
        throw new NotSupportedException("Cosmos documents are self-contained; load related data via embedded documents or an explicit query.");

    public Task LoadCollectionAsync<TEntity, TElement>(TEntity item,
        System.Linq.Expressions.Expression<Func<TEntity, IEnumerable<TElement>>> navigationProperty,
        System.Linq.Expressions.Expression<Func<TElement, bool>>? filter = null)
        where TEntity : class where TElement : class =>
        throw new NotSupportedException("Cosmos documents are self-contained; load related data via embedded documents or an explicit query.");

    public void Reload<TEntity>(TEntity item) where TEntity : class =>
        throw new NotSupportedException("The lean unit of work does not track read entities; re-query the document instead.");

    // -------------------------------------------------------------- dispose

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        _disposed = _disposed || disposing;
    }
}

/// <summary>The strongly-typed unit of work a consumer derives for its database.</summary>
public abstract class CosmosUnitOfWork<TDatabase>(IServiceProvider serviceProvider, CosmosClient client, Database database, CosmosModel model)
    : CosmosUnitOfWork(serviceProvider, client, database, model)
    where TDatabase : class;

/// <summary>A single staged Cosmos point write, runnable either concurrently (bulk) or inside a transactional batch.</summary>
/// <param name="ContainerName">The target container.</param>
/// <param name="PartitionKey">The partition key of the affected item.</param>
/// <param name="Execute">Runs the write as a standalone point operation (bulk).</param>
/// <param name="AddToBatch">Adds the write to a single-partition transactional batch.</param>
internal sealed record CosmosPendingWrite(
    string ContainerName,
    PartitionKey PartitionKey,
    Func<Container, CancellationToken, Task> Execute,
    Action<TransactionalBatch> AddToBatch);
