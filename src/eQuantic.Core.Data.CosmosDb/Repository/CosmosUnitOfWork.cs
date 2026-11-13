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
    private readonly List<(object Entity, CosmosEntityConfiguration Configuration, PartitionKey PartitionKey)> _generatedInserts = [];
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

    /// <summary>The model the client's serializer was built from (its renames and conversions).</summary>
    internal CosmosModel EntityModel => Model;

    internal Container GetContainer<TEntity>() => Database.GetContainer(Configuration<TEntity>().ContainerName);

    internal bool Maps(Type entityType) => Model.Configurations.ContainsKey(entityType);

    private QueryFilters? _queryFilters;
    private bool _queryFiltersResolved;
    private DataConventions? _conventions;

    /// <summary>The active write conventions — the registered <see cref="DataConventions" />, or the defaults.</summary>
    internal DataConventions Conventions =>
        _conventions ??= ServiceProvider.GetService(typeof(DataConventions)) as DataConventions ?? new DataConventions();

    /// <summary>The scope's service provider (handed to per-request convention accessors).</summary>
    internal IServiceProvider Services => ServiceProvider;

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

    // -------------------------------------------------------------- write staging (called by the repository/set)

    internal void StageInsert<TEntity>(TEntity item) where TEntity : class
    {
        EntityLifecycle.StampForInsert(item, Conventions, ServiceProvider);
        var configuration = Configuration<TEntity>();
        var partitionKey = configuration.GetPartitionKey(item);
        if (configuration.StoreGenerated.Count > 0)
        {
            _generatedInserts.Add((item, configuration, partitionKey));
        }

        _pending.Add(new CosmosPendingWrite(
            configuration.ContainerName,
            partitionKey,
            (container, cancellationToken) => container.CreateItemAsync(item, partitionKey, cancellationToken: cancellationToken),
            batch => batch.CreateItem(item)));
    }

    internal void StageUpsert<TEntity>(TEntity item) where TEntity : class
    {
        EntityLifecycle.StampForUpdate(item, Conventions, ServiceProvider);
        var configuration = Configuration<TEntity>();
        var partitionKey = configuration.GetPartitionKey(item);

        // A replace writes the whole document, so an entity with store-generated members first takes them from the
        // stored document and replaces it guarded by the _etag it read: an update never writes what the store
        // assigned.
        if (configuration.StoreGenerated.Count > 0)
        {
            var keeping = new CosmosKeepGeneratedUpsert<TEntity>(this, configuration, item, partitionKey);
            _pending.Add(new CosmosPendingWrite(configuration.ContainerName, partitionKey, keeping.ExecuteAsync, keeping.AddToBatch,
                keeping.PrepareAsync));
            return;
        }

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

    /// <summary>
    ///     Stages an update of the named members only (and of the update's lifecycle stamps): a patch of their paths,
    ///     guarded by the entity's <c>_etag</c> when it carries a token. Every other path keeps what the item holds.
    ///     Beyond the ten paths Cosmos DB takes in one patch, the paths go as several patches of the item in one
    ///     transactional batch, the first guarded by the <c>_etag</c>, applied whole or not at all; inside an explicit
    ///     transaction they join its batch.
    /// </summary>
    internal void StagePartialUpdate<TEntity>(TEntity item, IReadOnlyList<string> members) where TEntity : class
    {
        var configuration = Configuration<TEntity>();
        var properties = members.Select(member => typeof(TEntity).GetProperty(member)
                                                  ?? throw new NotSupportedException($"'{typeof(TEntity).Name}' has no member '{member}' to modify."))
            .ToList();
        foreach (var property in properties)
        {
            if (configuration.StoreGenerated.Any(generated => generated.Property.Name == property.Name))
            {
                throw new NotSupportedException($"'{property.Name}' is store-generated: the store assigns it, and an update never writes it.");
            }

            if (CosmosNaming.StoredName(property) == "id")
            {
                throw new NotSupportedException($"'{property.Name}' is the item's id, which identifies it and is never modified.");
            }
        }

        EntityLifecycle.StampForUpdate(item, Conventions, ServiceProvider);
        var stamps = EntityLifecycle.UpdateStamps(typeof(TEntity), Conventions, ServiceProvider)
            .Select(stamp => stamp.Member)
            .OfType<System.Reflection.PropertyInfo>();
        var operations = properties.Concat(stamps).DistinctBy(property => property.Name)
            .Select(property => PatchOperation.Set("/" + CosmosNaming.StoredName(property), property.GetValue(item)))
            .ToList();

        var id = configuration.GetId(item);
        var partitionKey = configuration.GetPartitionKey(item);
        var etag = configuration.GetETag(item);
        _pending.Add(new CosmosPendingWrite(
            configuration.ContainerName,
            partitionKey,
            (container, cancellationToken) => CosmosPatch.ApplyAsync<TEntity>(container, id, partitionKey, operations,
                filterPredicate: null, etag, cancellationToken),
            batch => CosmosPatch.AddTo(batch, id, operations, filterPredicate: null, etag)));
    }

    internal void StageDelete<TEntity>(TEntity item, string id) where TEntity : class
    {
        // A soft-delete entity's Remove stamps DeletedAt and stages a replace — the document survives.
        if (EntityLifecycle.TrySoftDelete(item, Conventions, ServiceProvider))
        {
            StageUpsert(item);
            return;
        }

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

        // The store's values go onto the entities before the writes serialize them.
        await AssignStoreGeneratedAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await Task.WhenAll(writes.Select(write =>
                write.Execute(Database.GetContainer(write.ContainerName), cancellationToken))).ConfigureAwait(false);
        }
        catch (CosmosException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // A create whose id (or unique key) is taken. Each write of the commit is its own: the others stand.
            throw new DuplicateKeyException(null, exception);
        }

        return writes.Count;
    }

    public int Commit(Action<SaveOptions> options) => Commit();
    public Task<int> CommitAsync(Action<SaveOptions> options, CancellationToken cancellationToken = default) => CommitAsync(cancellationToken);
    public int CommitAndRefreshChanges() => Commit();
    public int CommitAndRefreshChanges(Action<SaveOptions> options) => Commit();
    public Task<int> CommitAndRefreshChangesAsync(CancellationToken cancellationToken = default) => CommitAsync(cancellationToken);
    public Task<int> CommitAndRefreshChangesAsync(Action<SaveOptions> options, CancellationToken cancellationToken = default) => CommitAsync(cancellationToken);

    /// <summary>Discards every staged (uncommitted) write.</summary>
    public void RollbackChanges()
    {
        _pending.Clear();
        _generatedInserts.Clear();
    }

    private readonly object _clockGate = new();
    private Task<DateTime>? _clock;

    /// <summary>
    ///     Gives every staged insert the values the store assigns its store-generated members: the next numbers of
    ///     each counter (one atomic increment per counter for the whole commit) and the server's clock (read once,
    ///     the same instant for every insert of the commit).
    /// </summary>
    private async Task AssignStoreGeneratedAsync(CancellationToken cancellationToken)
    {
        lock (_clockGate)
        {
            _clock = null;
        }

        if (_generatedInserts.Count == 0)
        {
            return;
        }

        var inserts = _generatedInserts.ToList();
        _generatedInserts.Clear();
        await AssignStoreGeneratedAsync(inserts, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Assigns the store's values to inserts (or to an update that found no document, and so inserts).</summary>
    internal async Task AssignStoreGeneratedAsync(
        IReadOnlyList<(object Entity, CosmosEntityConfiguration Configuration, PartitionKey PartitionKey)> inserts,
        CancellationToken cancellationToken)
    {
        var members = inserts
            .SelectMany(insert => insert.Configuration.StoreGenerated.Select(member => (insert.Entity, insert.Configuration, Member: member)))
            .ToList();

        if (members.FirstOrDefault(insert => insert.Member.Value == Data.Modeling.StoreValue.Clock) is { Member: not null } first)
        {
            // One read of the server's clock per commit, shared by every insert of it (concurrent writes included).
            Task<DateTime> clock;
            lock (_clockGate)
            {
                var partitionKey = inserts.First(insert => ReferenceEquals(insert.Entity, first.Entity)).PartitionKey;
                clock = _clock ??= CosmosSequences.ServerClockAsync(Database.GetContainer(first.Configuration.ContainerName),
                    partitionKey, CancellationToken.None);
            }

            var now = await clock.WaitAsync(cancellationToken).ConfigureAwait(false);
            foreach (var insert in members.Where(insert => insert.Member.Value == Data.Modeling.StoreValue.Clock))
            {
                insert.Member.Assign(insert.Entity, now);
            }
        }

        foreach (var counter in members
                     .Where(insert => insert.Member.Value == Data.Modeling.StoreValue.Sequence)
                     .GroupBy(insert => CosmosSequences.CounterId(insert.Configuration.ContainerName, insert.Member.StoredName)))
        {
            var count = counter.Count();
            var last = await CosmosSequences.AdvanceAsync(Database, counter.Key, count, cancellationToken).ConfigureAwait(false);
            var next = last - count + 1;
            foreach (var insert in counter)
            {
                insert.Member.Assign(insert.Entity, next++);
            }
        }
    }

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

                // What the store assigns is read before the batch is built: the counters and the clock for the
                // inserts, the stored document (and its _etag) for a replace that keeps them.
                await AssignStoreGeneratedAsync(cancellationToken).ConfigureAwait(false);
                var container = Database.GetContainer(writes[0].ContainerName);
                foreach (var write in writes.Where(write => write.Prepare is not null))
                {
                    await write.Prepare!(container, cancellationToken).ConfigureAwait(false);
                }

                var batch = container.CreateTransactionalBatch(writes[0].PartitionKey);
                foreach (var write in writes)
                {
                    write.AddToBatch(batch);
                }

                using var response = await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.Conflict
                    || response.Any(operation => operation.StatusCode == System.Net.HttpStatusCode.Conflict))
                {
                    // The batch is atomic: a taken id (or unique key) left nothing of it written.
                    throw new DuplicateKeyException(
                        "The store refused the transaction: a key or a unique key it holds is already taken, and nothing of it was written.",
                        null, new InvalidOperationException($"The Cosmos transactional batch failed with status {response.StatusCode}."));
                }

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
        _generatedInserts.Clear();
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
/// <param name="Prepare">Reads what the write needs before it joins a batch (the stored document it keeps values of), or <c>null</c>.</param>
internal sealed record CosmosPendingWrite(
    string ContainerName,
    PartitionKey PartitionKey,
    Func<Container, CancellationToken, Task> Execute,
    Action<TransactionalBatch> AddToBatch,
    Func<Container, CancellationToken, Task>? Prepare = null);

/// <summary>
///     A replace of an entity with store-generated members that keeps the stored values of those members: it reads
///     the stored document, copies them onto the entity, and replaces it guarded by the <c>_etag</c> it read (the
///     entity's own token when it carries one). A concurrent writer between the read and the replace makes the
///     replace fail, and it reads again — unless the entity carries a token, whose conflict is the caller's to see.
///     When no document is stored, the write is an insert: the store assigns the members, as on any insert.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
internal sealed class CosmosKeepGeneratedUpsert<TEntity>(CosmosUnitOfWork unitOfWork, CosmosEntityConfiguration configuration,
    TEntity item, PartitionKey partitionKey) where TEntity : class
{
    private const int Attempts = 10;
    private readonly string _id = configuration.GetId(item);
    private readonly string? _token = configuration.GetETag(item);
    private string? _storedETag;
    private bool _missing;

    /// <summary>Reads the stored document and takes its store-generated values (or the store's new ones when there is none).</summary>
    public async Task PrepareAsync(Container container, CancellationToken cancellationToken)
    {
        try
        {
            var stored = await container.ReadItemAsync<TEntity>(_id, partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);
            _storedETag = stored.ETag;
            _missing = false;
            foreach (var member in configuration.StoreGenerated)
            {
                member.Property.SetValue(item, member.Property.GetValue(stored.Resource));
            }
        }
        catch (CosmosException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound && _token is null)
        {
            _storedETag = null;
            _missing = true;
            await unitOfWork.AssignStoreGeneratedAsync([(item, configuration, partitionKey)], cancellationToken).ConfigureAwait(false);
        }
        catch (CosmosException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // The entity carries a token for a document that is gone: the replace below answers the conflict.
            _storedETag = _token;
            _missing = false;
        }
    }

    /// <summary>Runs the write on its own (bulk), reading again after losing a race with another writer.</summary>
    public async Task ExecuteAsync(Container container, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await PrepareAsync(container, cancellationToken).ConfigureAwait(false);
            try
            {
                if (_missing)
                {
                    await container.CreateItemAsync(item, partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await container.ReplaceItemAsync(item, _id, partitionKey,
                        new ItemRequestOptions { IfMatchEtag = _token ?? _storedETag }, cancellationToken).ConfigureAwait(false);
                }

                return;
            }
            catch (CosmosException exception) when (_token is null && attempt < Attempts
                                                    && exception.StatusCode is System.Net.HttpStatusCode.PreconditionFailed
                                                        or System.Net.HttpStatusCode.Conflict)
            {
                // Another writer changed (or created) the document between the read and the write: read it again.
            }
        }
    }

    /// <summary>Adds the prepared write to a transactional batch.</summary>
    public void AddToBatch(TransactionalBatch batch)
    {
        if (_missing)
        {
            batch.CreateItem(item);
            return;
        }

        batch.ReplaceItem(_id, item, new TransactionalBatchItemRequestOptions { IfMatchEtag = _token ?? _storedETag });
    }
}
