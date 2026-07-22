using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using eQuantic.Core.Data.Diagnostics;
using eQuantic.Core.Data.Query;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

namespace eQuantic.Core.Data.MongoDb.Repository;

/// <summary>
///     The native MongoDB unit of work. Entity writes (<c>Add</c>/<c>Modify</c>/<c>Remove</c>) are staged as
///     <c>WriteModel</c>s and flushed on <see cref="CommitAsync(System.Threading.CancellationToken)" /> as a
///     single ordered bulk write per collection — no change tracking, snapshotting or change detection.
///     Explicit multi-document transactions are a separate concern: open one with
///     <see cref="BeginTransactionAsync" /> and the commit flushes inside it.
/// </summary>
public abstract class MongoUnitOfWork : IQueryableUnitOfWork, IUnionQueryRunner
{
    protected readonly IServiceProvider ServiceProvider;
    protected readonly IMongoClient Client;
    protected readonly IMongoDatabase Database;

    private readonly Dictionary<Type, IPendingCollectionWrites> _pending = new();
    private IClientSessionHandle? _session;
    private bool _disposed;

    protected MongoUnitOfWork(IServiceProvider serviceProvider, IMongoClient client, IMongoDatabase database)
    {
        ServiceProvider = serviceProvider;
        Client = client;
        Database = database;
    }

    /// <summary>Gets the collection name used for <typeparamref name="TEntity" /> (the type name by default).</summary>
    protected virtual string CollectionName<TEntity>() => typeof(TEntity).Name;

    internal IMongoCollection<TEntity> GetCollection<TEntity>() =>
        Database.GetCollection<TEntity>(CollectionName<TEntity>());

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

    // -------------------------------------------------------------- union reads

    private static readonly MethodInfo ShapedBranchMethod = typeof(MongoUnitOfWork)
        .GetMethod(nameof(ShapedBranch), BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <inheritdoc />
    /// <remarks>
    ///     Runs on the server as one aggregation: the first branch's pipeline plus a <c>$unionWith</c> per
    ///     additional branch — each branch's filters (the entity's global filter included unless the branch opted
    ///     out) apply inside its own pipeline. <see cref="UnionQuery.Distinct{TResult}" /> deduplicates with a
    ///     <c>$group</c> over the combined shape; ordering and paging apply to the combined rows.
    /// </remarks>
    public async Task<IReadOnlyList<TResult>> UnionAsync<TResult>(UnionQuery<TResult> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // The same contract as everywhere else: members-or-constants projections, one shape across branches.
        var targets = UnionInterpreter.InterpretAll(query.Branches)[0].Bindings.Select(binding => binding.Target).ToList();

        using var activity = DataActivitySource.Instance.StartActivity("mongodb.union", ActivityKind.Client);
        if (activity is not null)
        {
            activity.SetTag("db.system", "mongodb");
            activity.SetTag("equantic.union_branches", query.Branches.Count);
            activity.SetTag("equantic.union_all", query.All);
        }

        IQueryable<TResult>? combined = null;
        foreach (var branch in query.Branches)
        {
            var shaped = (IQueryable<TResult>)ShapedBranchMethod
                .MakeGenericMethod(branch.EntityType, typeof(TResult))
                .Invoke(this, [branch])!;
            combined = combined is null ? shaped : combined.Concat(shaped);
        }

        if (!query.All)
        {
            combined = combined!.Distinct();
        }

        combined = OrderCombined(combined!, query.Order, targets);

        if (query.Offset is not null && query.Limit is null)
        {
            throw new NotSupportedException("Skip without Take is not supported on a union; add Take(...).");
        }

        if (query.Offset is { } offset)
        {
            combined = combined.Skip(offset);
        }

        if (query.Limit is { } limit)
        {
            combined = combined.Take(limit);
        }

        return await combined.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One branch's pipeline: the session-bound collection, its filters, projected into the common shape.</summary>
    private IQueryable<TResult> ShapedBranch<TDocument, TResult>(UnionBranch branch)
        where TDocument : class
    {
        var collection = GetCollection<TDocument>();
        var query = _session is null ? collection.AsQueryable() : collection.AsQueryable(_session);

        if (!branch.IgnoreQueryFilters && GlobalFilter<TDocument>() is { } global)
        {
            query = query.Where(global);
        }

        foreach (var filter in branch.Filters)
        {
            query = query.Where((Expression<Func<TDocument, bool>>)filter);
        }

        return query.Select((Expression<Func<TDocument, TResult>>)branch.Projection);
    }

    private static IQueryable<TResult> OrderCombined<TResult>(IQueryable<TResult> combined,
        IReadOnlyList<UnionOrder> order, IReadOnlyList<string> targets)
    {
        for (var index = 0; index < order.Count; index++)
        {
            if (!targets.Contains(order[index].Member, StringComparer.OrdinalIgnoreCase))
            {
                throw new NotSupportedException($"The union projects no member '{order[index].Member}' to order by.");
            }

            var row = Expression.Parameter(typeof(TResult), "row");
            var member = Expression.PropertyOrField(row, order[index].Member);
            var selector = Expression.Lambda(member, row);
            var method = (index == 0, order[index].Descending) switch
            {
                (true, false) => nameof(Queryable.OrderBy),
                (true, true) => nameof(Queryable.OrderByDescending),
                (false, false) => nameof(Queryable.ThenBy),
                (false, true) => nameof(Queryable.ThenByDescending),
            };
            combined = combined.Provider.CreateQuery<TResult>(Expression.Call(
                typeof(Queryable), method, [typeof(TResult), member.Type],
                combined.Expression, Expression.Quote(selector)));
        }

        return combined;
    }

    // -------------------------------------------------------------- write staging (called by MongoSet)

    internal void StageInsert<TEntity>(TEntity item) where TEntity : class =>
        Buffer<TEntity>().Add(new InsertOneModel<TEntity>(item));

    internal void StageReplace<TEntity>(TEntity item) where TEntity : class =>
        Buffer<TEntity>().Add(new ReplaceOneModel<TEntity>(IdFilter(item), item) { IsUpsert = false });

    internal void StageUpsert<TEntity>(TEntity item) where TEntity : class =>
        Buffer<TEntity>().Add(new ReplaceOneModel<TEntity>(IdFilter(item), item) { IsUpsert = true });

    internal void StageDelete<TEntity>(TEntity item) where TEntity : class =>
        Buffer<TEntity>().Add(new DeleteOneModel<TEntity>(IdFilter(item)));

    private PendingCollectionWrites<TEntity> Buffer<TEntity>() where TEntity : class
    {
        if (!_pending.TryGetValue(typeof(TEntity), out var pending))
        {
            pending = new PendingCollectionWrites<TEntity>(GetCollection<TEntity>());
            _pending[typeof(TEntity)] = pending;
        }

        return (PendingCollectionWrites<TEntity>)pending;
    }

    private static FilterDefinition<TEntity> IdFilter<TEntity>(TEntity item)
    {
        var idMember = BsonClassMap.LookupClassMap(typeof(TEntity)).IdMemberMap
                       ?? throw new InvalidOperationException(
                           $"'{typeof(TEntity).Name}' has no mapped id member; a document entity must expose one (e.g. an Id property).");
        return Builders<TEntity>.Filter.Eq("_id", idMember.Getter(item!));
    }

    // -------------------------------------------------------------- commit (the single execution point)

    public int Commit() => CommitAsync().GetAwaiter().GetResult();

    public async Task<int> CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_pending.Count == 0)
        {
            return 0;
        }

        using var activity = DataActivitySource.Instance.StartActivity("mongodb.commit", ActivityKind.Client);
        activity?.SetTag("db.system", "mongodb");
        activity?.SetTag("equantic.writes", _pending.Values.Sum(pending => pending.Count));

        long affected = 0;
        foreach (var pending in _pending.Values)
        {
            affected += await pending.FlushAsync(_session, cancellationToken).ConfigureAwait(false);
        }

        _pending.Clear();
        return (int)affected;
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

    // -------------------------------------------------------------- explicit transactions (sessions)

    /// <summary>Opens a multi-document transaction; subsequent commits flush inside it. Requires a replica set.</summary>
    public async Task BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        _session ??= await Client.StartSessionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        _session.StartTransaction();
    }

    /// <summary>Commits the active transaction.</summary>
    public async Task CommitTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_session is { IsInTransaction: true })
        {
            await _session.CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Aborts the active transaction and discards staged writes.</summary>
    public async Task RollbackTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_session is { IsInTransaction: true })
        {
            await _session.AbortTransactionAsync(cancellationToken).ConfigureAwait(false);
        }

        RollbackChanges();
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
        new MongoSet<TEntity>(this, GetCollection<TEntity>());

    internal IClientSessionHandle? Session => _session;

    // -------------------------------------------------------------- change-tracking hooks (mapped to the lean model)

    /// <summary>Stages the current entity as a replace (the document store has no separate "apply values" step).</summary>
    public void ApplyCurrentValues<TEntity>(TEntity original, TEntity current) where TEntity : class, IEntity =>
        StageReplace(current);

    /// <summary>No-op: the lean model does not track read entities, so there is nothing to attach.</summary>
    public void Attach<TEntity>(TEntity item) where TEntity : class, IEntity { }

    /// <summary>Stages the entity as a replace.</summary>
    public void SetModified<TEntity>(TEntity item) where TEntity : class => StageReplace(item);

    public void LoadCollection<TEntity, TElement>(TEntity item,
        System.Linq.Expressions.Expression<Func<TEntity, IEnumerable<TElement>>> navigationProperty,
        System.Linq.Expressions.Expression<Func<TElement, bool>>? filter = null)
        where TEntity : class where TElement : class =>
        throw new NotSupportedException("MongoDB documents are self-contained; load related data via embedded documents or an explicit query.");

    public Task LoadCollectionAsync<TEntity, TElement>(TEntity item,
        System.Linq.Expressions.Expression<Func<TEntity, IEnumerable<TElement>>> navigationProperty,
        System.Linq.Expressions.Expression<Func<TElement, bool>>? filter = null)
        where TEntity : class where TElement : class =>
        throw new NotSupportedException("MongoDB documents are self-contained; load related data via embedded documents or an explicit query.");

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
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _session?.Dispose();
        }

        _disposed = true;
    }
}

/// <summary>The strongly-typed unit of work a consumer derives for its database.</summary>
public abstract class MongoUnitOfWork<TDatabase>(IServiceProvider serviceProvider, IMongoClient client, IMongoDatabase database)
    : MongoUnitOfWork(serviceProvider, client, database)
    where TDatabase : class;
