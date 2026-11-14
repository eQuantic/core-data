using System.Diagnostics;
using System.Linq.Expressions;
using System.Net;
using System.Runtime.ExceptionServices;
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
///     <see cref="CommitTransactionAsync" />, which takes at most 100 operations.
///     <para>
///         A write guarded by the entity's concurrency token that another writer got to first — refused on its
///         <c>_etag</c> (412), or finding the document gone (404) — fails the commit or the transaction with
///         <see cref="ConcurrencyConflictException" />, as on every engine; a taken key or unique key (409) with
///         <see cref="DuplicateKeyException" />.
///     </para>
/// </summary>
public abstract partial class CosmosUnitOfWork : IQueryableUnitOfWork
{
    /// <summary>The operations Cosmos DB takes in one transactional batch.</summary>
    internal const int MaxBatchOperations = 100;

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
                keeping.PrepareAsync, Guarded: keeping.Guarded));
            return;
        }

        // An entity carrying its concurrency token stages a conditional replace: a concurrent change (or removal)
        // makes the commit fail with ConcurrencyConflictException instead of silently winning.
        if (configuration.GetETag(item) is { } etag)
        {
            var id = configuration.GetId(item);
            _pending.Add(new CosmosPendingWrite(
                configuration.ContainerName,
                partitionKey,
                (container, cancellationToken) => container.ReplaceItemAsync(item, id, partitionKey,
                    new ItemRequestOptions { IfMatchEtag = etag }, cancellationToken),
                batch => batch.ReplaceItem(id, item, new TransactionalBatchItemRequestOptions { IfMatchEtag = etag }),
                Guarded: true));
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
            batch => CosmosPatch.AddTo(batch, id, operations, filterPredicate: null, etag),
            Operations: CosmosPatch.OperationsFor(operations.Count),
            Guarded: etag is not null));
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

        // An entity carrying its concurrency token stages a delete guarded by it, as its replace is: removing an item
        // someone else changed (412) or already removed (404) fails the commit with ConcurrencyConflictException.
        if (configuration.GetETag(item) is { } etag)
        {
            _pending.Add(new CosmosPendingWrite(
                configuration.ContainerName,
                partitionKey,
                async (container, cancellationToken) =>
                {
                    using var response = await container.DeleteItemStreamAsync(id, partitionKey,
                        new ItemRequestOptions { IfMatchEtag = etag }, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                },
                batch => batch.DeleteItem(id, new TransactionalBatchItemRequestOptions { IfMatchEtag = etag }),
                Guarded: true));
            return;
        }

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

        var inserts = TakeGeneratedInserts();
        var clock = new CosmosServerClock();
        _commitClock = clock;
        var failures = new ExceptionDispatchInfo?[writes.Count];
        try
        {
            // The store's values go onto the entities before the writes serialize them.
            await AssignStoreGeneratedAsync(inserts, clock, cancellationToken).ConfigureAwait(false);

            // Each write of the commit is its own: one that fails leaves the others standing, and every one of them is
            // let finish before the first failure is answered.
            await Task.WhenAll(writes.Select(async (write, index) =>
            {
                try
                {
                    await write.Execute(Database.GetContainer(write.ContainerName), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures[index] = ExceptionDispatchInfo.Capture(exception);
                }
            })).ConfigureAwait(false);
        }
        finally
        {
            _commitClock = null;
        }

        var failed = Array.FindIndex(failures, failure => failure is not null);
        if (failed < 0)
        {
            return writes.Count;
        }

        switch (failures[failed]!.SourceException)
        {
            case CosmosException { StatusCode: HttpStatusCode.Conflict } conflict:
                // A create whose id (or unique key) is taken, or an update onto another item's unique key.
                throw new DuplicateKeyException(null, conflict);
            case CosmosException refusal when LostToAnotherWriter(writes[failed], refusal.StatusCode):
                throw new ConcurrencyConflictException(writes.Count, failures.Count(failure => failure is null));
            default:
                failures[failed]!.Throw();
                throw new UnreachableException();
        }
    }

    /// <summary>
    ///     Whether a refusal says another writer got to the item first: its <c>_etag</c> moved (412), or a write guarded by
    ///     the entity's concurrency token found it gone (404).
    /// </summary>
    private static bool LostToAnotherWriter(CosmosPendingWrite write, HttpStatusCode status) =>
        status == HttpStatusCode.PreconditionFailed || (write.Guarded && status == HttpStatusCode.NotFound);

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

    private CosmosServerClock? _commitClock;

    /// <summary>
    ///     The clock of the commit (or transaction) running now, shared by its inserts: an update of it that finds no
    ///     document inserts with the same instant. Outside one, a clock of its own.
    /// </summary>
    internal CosmosServerClock CommitClock => _commitClock ?? new CosmosServerClock();

    /// <summary>The staged inserts waiting for the store's values, handed over once (the list is emptied).</summary>
    private List<(object Entity, CosmosEntityConfiguration Configuration, PartitionKey PartitionKey)> TakeGeneratedInserts()
    {
        var inserts = _generatedInserts.ToList();
        _generatedInserts.Clear();
        return inserts;
    }

    /// <summary>
    ///     Assigns the store's values to inserts (or to an update that found no document, and so inserts): the next
    ///     numbers of each counter (one atomic increment per counter for all of them) and the server's clock, read when
    ///     first needed by the operation the <paramref name="clock" /> belongs to and given to every insert of it.
    /// </summary>
    internal async Task AssignStoreGeneratedAsync(
        IReadOnlyList<(object Entity, CosmosEntityConfiguration Configuration, PartitionKey PartitionKey)> inserts,
        CosmosServerClock clock, CancellationToken cancellationToken)
    {
        if (inserts.Count == 0)
        {
            return;
        }

        var members = inserts
            .SelectMany(insert => insert.Configuration.StoreGenerated.Select(member => (insert.Entity, insert.Configuration, Member: member)))
            .ToList();

        if (members.FirstOrDefault(insert => insert.Member.Value == Data.Modeling.StoreValue.Clock) is { Member: not null } first)
        {
            // One read of the server's clock per operation, shared by every insert of it (concurrent writes included).
            var partitionKey = inserts.First(insert => ReferenceEquals(insert.Entity, first.Entity)).PartitionKey;
            var now = await clock.ReadAsync(Database.GetContainer(first.Configuration.ContainerName), partitionKey, cancellationToken)
                .ConfigureAwait(false);
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

    /// <summary>
    ///     Runs the deferred writes as one single-partition <see cref="TransactionalBatch" />: all of them or none. A
    ///     transaction that crosses partitions, or needs more than the 100 operations a batch takes (a
    ///     member-scoped modify of more than ten members is one operation per ten of them), is refused with
    ///     <see cref="NotSupportedException" /> before anything is read, numbered or written, and its writes are
    ///     discarded. A batch the store refuses throws <see cref="DuplicateKeyException" /> for a taken key,
    ///     <see cref="ConcurrencyConflictException" /> for a write another writer got to first, and otherwise a
    ///     <see cref="CosmosException" /> with the failing operation's status and the store's reason.
    /// </summary>
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
                var inserts = TakeGeneratedInserts();
                if (writes.GroupBy(write => (write.ContainerName, write.PartitionKey.ToString())).Skip(1).Any())
                {
                    throw new NotSupportedException(
                        "A Cosmos transaction is limited to a single container and partition key; commit cross-partition writes without a transaction.");
                }

                var operations = writes.Sum(write => write.Operations);
                if (operations > MaxBatchOperations)
                {
                    throw new NotSupportedException(
                        $"A Cosmos transaction runs as one transactional batch, which takes at most {MaxBatchOperations} operations; " +
                        $"this one needs {operations} operations for {writes.Count} writes (a member-scoped modify of more than " +
                        $"{CosmosPatch.MaxOperations} members is one operation per {CosmosPatch.MaxOperations} of them). Nothing was " +
                        "written: commit it as several transactions, or without one.");
                }

                using var activity = DataActivitySource.Instance.StartActivity("cosmosdb.commit_transaction", ActivityKind.Client);
                activity?.SetTag("db.system", "cosmosdb");
                activity?.SetTag("equantic.writes", writes.Count);

                var clock = new CosmosServerClock();
                _commitClock = clock;
                try
                {
                    // What the store assigns is read before the batch is built: the counters and the clock for the
                    // inserts, the stored document (and its _etag) for a replace that keeps them.
                    await AssignStoreGeneratedAsync(inserts, clock, cancellationToken).ConfigureAwait(false);
                    var container = Database.GetContainer(writes[0].ContainerName);
                    foreach (var write in writes.Where(write => write.Prepare is not null))
                    {
                        await write.Prepare!(container, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _commitClock = null;
                }

                // Which write each operation of the batch belongs to, to tell what its refusal means.
                var owners = new List<int>(operations);
                var batch = Database.GetContainer(writes[0].ContainerName).CreateTransactionalBatch(writes[0].PartitionKey);
                for (var index = 0; index < writes.Count; index++)
                {
                    writes[index].AddToBatch(batch);
                    owners.AddRange(Enumerable.Repeat(index, writes[index].Operations));
                }

                using var response = await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw BatchRefusal(response, writes, owners);
                }
            }
        }
        finally
        {
            _inTransaction = false;
            await EndTransactionLocksAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     What a refused transactional batch means, from the operation that failed (the others answer 424, failed
    ///     dependency) and the write it belongs to. Nothing of the batch was written.
    /// </summary>
    private static Exception BatchRefusal(TransactionalBatchResponse response, IReadOnlyList<CosmosPendingWrite> writes,
        IReadOnlyList<int> owners)
    {
        var failed = -1;
        for (var index = 0; index < response.Count; index++)
        {
            if (!response[index].IsSuccessStatusCode && response[index].StatusCode != HttpStatusCode.FailedDependency)
            {
                failed = index;
                break;
            }
        }

        var status = failed >= 0 ? response[failed].StatusCode : response.StatusCode;
        var driver = new CosmosException(
            $"The Cosmos transactional batch of {owners.Count} operations failed with status {status}" +
            (failed >= 0 ? $" at operation {failed}" : string.Empty) + $", and nothing of it was written: {response.ErrorMessage}",
            status, 0, response.ActivityId, response.RequestCharge);

        if (status == HttpStatusCode.Conflict)
        {
            return new DuplicateKeyException(
                "The store refused the transaction: a key or a unique key it holds is already taken, and nothing of it was written.",
                null, driver);
        }

        var write = failed >= 0 && failed < owners.Count ? writes[owners[failed]] : null;
        if (status == HttpStatusCode.PreconditionFailed || (write?.Guarded == true && status == HttpStatusCode.NotFound))
        {
            return new ConcurrencyConflictException(writes.Count, 0);
        }

        return driver;
    }

    /// <summary>Aborts the transaction and discards its deferred writes.</summary>
    public Task RollbackTransactionAsync(CancellationToken cancellationToken = default)
    {
        _pending.Clear();
        _generatedInserts.Clear();
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
        if (disposing && !_disposed)
        {
            ReleaseNamedLocks();
        }

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
/// <param name="Operations">The operations the write adds to a transactional batch.</param>
/// <param name="Guarded">
///     Whether the write is guarded by the entity's concurrency token: finding the document gone, it lost to another
///     writer.
/// </param>
internal sealed record CosmosPendingWrite(
    string ContainerName,
    PartitionKey PartitionKey,
    Func<Container, CancellationToken, Task> Execute,
    Action<TransactionalBatch> AddToBatch,
    Func<Container, CancellationToken, Task>? Prepare = null,
    int Operations = 1,
    bool Guarded = false);

/// <summary>
///     One read of the server's clock shared by the inserts of one write operation — a commit, a transaction, an
///     <c>InsertManyAsync</c>, an upsert: read when the first of them needs it, never earlier, and forgotten when the read
///     fails, so that the next need reads it again.
/// </summary>
internal sealed class CosmosServerClock
{
    private readonly object _gate = new();
    private Task<DateTime>? _read;

    /// <summary>The server's clock, read by a query scoped to one partition of a container the first time it is asked for.</summary>
    /// <param name="container">The container to read it in.</param>
    /// <param name="partitionKey">The partition to scope the query to.</param>
    /// <param name="cancellationToken">A cancellation token (the read itself, shared by concurrent inserts, is never cut off).</param>
    public async Task<DateTime> ReadAsync(Container container, PartitionKey partitionKey, CancellationToken cancellationToken)
    {
        Task<DateTime> read;
        lock (_gate)
        {
            read = _read ??= CosmosSequences.ServerClockAsync(container, partitionKey, CancellationToken.None);
        }

        try
        {
            return await read.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch when (read.IsFaulted || read.IsCanceled)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_read, read))
                {
                    _read = null;
                }
            }

            throw;
        }
    }
}

/// <summary>
///     A replace of an entity with store-generated members that keeps the stored values of those members: it reads
///     the stored document, copies them onto the entity, and replaces it guarded by the <c>_etag</c> it read (the
///     entity's own token when it carries one). A concurrent writer between the read and the replace makes the
///     replace fail, and it reads again — unless the entity carries a token, whose conflict is the caller's to see.
///     When no document is stored, the write is an insert: the store assigns the members, as on any insert, once for
///     all its attempts. A create refused on its id (a concurrent insert of the same document) reads again; one refused
///     on another unique key is not retried, since no attempt can take that key.
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
    private object?[]? _assigned;

    /// <summary>Whether the entity carries its concurrency token, so that a document gone is another writer's doing.</summary>
    public bool Guarded => _token is not null;

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
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound && _token is null)
        {
            _storedETag = null;
            _missing = true;
            await AssignOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // The entity carries a token for a document that is gone: the replace below answers the conflict.
            _storedETag = _token;
            _missing = false;
        }
    }

    /// <summary>
    ///     Gives the insert the store's values: the first time, the counters' next numbers and the clock; on a later
    ///     attempt, the same ones again, so an attempt that is retried leaves no gap of its own.
    /// </summary>
    private async Task AssignOnceAsync(CancellationToken cancellationToken)
    {
        if (_assigned is { } values)
        {
            for (var index = 0; index < values.Length; index++)
            {
                configuration.StoreGenerated[index].Property.SetValue(item, values[index]);
            }

            return;
        }

        await unitOfWork.AssignStoreGeneratedAsync([(item, configuration, partitionKey)], unitOfWork.CommitClock, cancellationToken)
            .ConfigureAwait(false);
        _assigned = configuration.StoreGenerated.Select(member => member.Property.GetValue(item)).ToArray();
    }

    /// <summary>Runs the write on its own (bulk), reading again after losing a race with another writer.</summary>
    public async Task ExecuteAsync(Container container, CancellationToken cancellationToken)
    {
        CosmosException? lostCreate = null;
        for (var attempt = 1; ; attempt++)
        {
            await PrepareAsync(container, cancellationToken).ConfigureAwait(false);
            if (!_missing)
            {
                lostCreate = null;
            }
            else if (lostCreate is not null)
            {
                // The create was refused while no document holds this id: another unique key is taken, and no retry
                // can take it.
                throw lostCreate;
            }

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
            catch (CosmosException exception) when (_token is null && attempt < Attempts && _missing
                                                    && exception.StatusCode == HttpStatusCode.Conflict)
            {
                // A concurrent insert of this document, or another unique key taken: the next read says which.
                lostCreate = exception;
            }
            catch (CosmosException exception) when (_token is null && attempt < Attempts && !_missing
                                                    && exception.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
            {
                // Another writer changed (or removed) the document between the read and the write: read it again.
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
