using System.Linq.Expressions;
using System.Reflection;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Read;
using eQuantic.Linq.Specification;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

namespace eQuantic.Core.Data.MongoDb.Repository;

/// <summary>
///     The native MongoDB read repository — synchronous and asynchronous. Every read shapes the collection's
///     LINQ queryable (translated by the driver to an aggregation pipeline) with a single
///     <see cref="QueryOptions{TEntity}" />, then executes through the driver's LINQ provider.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TKey">The key type.</typeparam>
public abstract class MongoReadRepository<TEntity, TKey> :
    IQueryableReadRepository<TEntity, TKey>,
    IAsyncQueryableReadRepository<TEntity, TKey>,
    IExplainableRepository<TEntity>
    where TEntity : class, IEntity<TKey>
{
    /// <summary>The unit of work backing this repository.</summary>
    protected readonly MongoUnitOfWork UnitOfWork;

    /// <summary>The entity's collection.</summary>
    protected readonly IMongoCollection<TEntity> Collection;

    private readonly MemberInfo _idMember;
    private readonly Type _idType;

    /// <summary>Initializes the repository over a unit of work.</summary>
    /// <param name="unitOfWork">The queryable unit of work (a <see cref="MongoUnitOfWork" />).</param>
    protected MongoReadRepository(IQueryableUnitOfWork unitOfWork)
    {
        UnitOfWork = unitOfWork as MongoUnitOfWork
                     ?? throw new ArgumentException(
                         $"The unit of work must be a {nameof(MongoUnitOfWork)}.", nameof(unitOfWork));
        Collection = UnitOfWork.GetCollection<TEntity>();

        var idMemberMap = BsonClassMap.LookupClassMap(typeof(TEntity)).IdMemberMap
                          ?? throw new InvalidOperationException(
                              $"'{typeof(TEntity).Name}' has no mapped id member; a document entity must expose one (e.g. an Id property).");
        _idMember = idMemberMap.MemberInfo;
        _idType = idMemberMap.MemberType;
    }

    // ---------------------------------------------------------------- synchronous reads

    /// <inheritdoc />
    public TEntity? Get(TKey id, QueryOptions<TEntity>? options = null) =>
        Query(options, query => query.Where(IdPredicate(id))).SingleOrDefault();

    /// <inheritdoc />
    public IEnumerable<TEntity> GetAll(QueryOptions<TEntity>? options = null) =>
        Query(options).ToList();

    /// <inheritdoc />
    public IEnumerable<TEntity> GetFiltered(Expression<Func<TEntity, bool>> filter, QueryOptions<TEntity>? options = null) =>
        Query(options, query => query.Where(NotNull(filter))).ToList();

    /// <inheritdoc />
    public IEnumerable<TEntity> AllMatching(ISpecification<TEntity> specification, QueryOptions<TEntity>? options = null) =>
        Query(options, query => query.Where(NotNull(specification).SatisfiedBy())).ToList();

    /// <inheritdoc />
    public IEnumerable<TResult> GetMapped<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null) =>
        Query(options).Select(NotNull(map)).ToList();

    /// <inheritdoc />
    public TEntity? GetFirst(QueryOptions<TEntity> options) =>
        Query(options).FirstOrDefault();

    /// <inheritdoc />
    public TResult? GetFirstMapped<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity> options) =>
        Query(options).Select(NotNull(map)).FirstOrDefault();

    /// <inheritdoc />
    public TEntity? GetSingle(QueryOptions<TEntity> options) =>
        Query(options).SingleOrDefault();

    /// <inheritdoc />
    public PagedResult<TEntity> GetPaged(PageRequest page, QueryOptions<TEntity>? options = null)
    {
        NotNull(page);
        var query = Query(options);
        var total = query.LongCount();
        var items = Ordered(query, options).Skip(page.Skip).Take(page.Take).ToList();
        return new PagedResult<TEntity>(items, total, page.PageIndex, page.PageSize);
    }

    /// <inheritdoc />
    public PagedResult<TResult> GetPaged<TResult>(PageRequest page, Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null)
    {
        NotNull(page);
        var query = Query(options);
        var total = query.LongCount();
        var items = Ordered(query, options).Skip(page.Skip).Take(page.Take).Select(NotNull(map)).ToList();
        return new PagedResult<TResult>(items, total, page.PageIndex, page.PageSize);
    }

    /// <inheritdoc />
    public long Count(QueryOptions<TEntity>? options = null) => Query(options).LongCount();

    /// <inheritdoc />
    public bool Any(QueryOptions<TEntity>? options = null) => Query(options).Any();

    /// <inheritdoc />
    public bool All(Expression<Func<TEntity, bool>> predicate, QueryOptions<TEntity>? options = null) =>
        !Query(options).Any(Negate(NotNull(predicate)));

    /// <inheritdoc />
    public int Sum(Expression<Func<TEntity, int>> selector, QueryOptions<TEntity>? options = null) => Query(options).Sum(NotNull(selector));

    /// <inheritdoc />
    public int? Sum(Expression<Func<TEntity, int?>> selector, QueryOptions<TEntity>? options = null) => Query(options).Sum(NotNull(selector));

    /// <inheritdoc />
    public long Sum(Expression<Func<TEntity, long>> selector, QueryOptions<TEntity>? options = null) => Query(options).Sum(NotNull(selector));

    /// <inheritdoc />
    public long? Sum(Expression<Func<TEntity, long?>> selector, QueryOptions<TEntity>? options = null) => Query(options).Sum(NotNull(selector));

    /// <inheritdoc />
    public double Sum(Expression<Func<TEntity, double>> selector, QueryOptions<TEntity>? options = null) => Query(options).Sum(NotNull(selector));

    /// <inheritdoc />
    public double? Sum(Expression<Func<TEntity, double?>> selector, QueryOptions<TEntity>? options = null) => Query(options).Sum(NotNull(selector));

    /// <inheritdoc />
    public float Sum(Expression<Func<TEntity, float>> selector, QueryOptions<TEntity>? options = null) => Query(options).Sum(NotNull(selector));

    /// <inheritdoc />
    public float? Sum(Expression<Func<TEntity, float?>> selector, QueryOptions<TEntity>? options = null) => Query(options).Sum(NotNull(selector));

    /// <inheritdoc />
    public decimal Sum(Expression<Func<TEntity, decimal>> selector, QueryOptions<TEntity>? options = null) => Query(options).Sum(NotNull(selector));

    /// <inheritdoc />
    public decimal? Sum(Expression<Func<TEntity, decimal?>> selector, QueryOptions<TEntity>? options = null) => Query(options).Sum(NotNull(selector));

    // ---------------------------------------------------------------- asynchronous reads

    /// <inheritdoc />
    public async Task<TEntity?> GetAsync(TKey id, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await Query(options, query => query.Where(IdPredicate(id))).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IEnumerable<TEntity>> GetAllAsync(QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await Query(options).ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IEnumerable<TEntity>> GetFilteredAsync(Expression<Func<TEntity, bool>> filter, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await Query(options, query => query.Where(NotNull(filter))).ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IEnumerable<TEntity>> AllMatchingAsync(ISpecification<TEntity> specification, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await Query(options, query => query.Where(NotNull(specification).SatisfiedBy())).ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IEnumerable<TResult>> GetMappedAsync<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await Query(options).Select(NotNull(map)).ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<TEntity?> GetFirstAsync(QueryOptions<TEntity> options, CancellationToken cancellationToken = default) =>
        await Query(options).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<TResult?> GetFirstMappedAsync<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity> options, CancellationToken cancellationToken = default) =>
        await Query(options).Select(NotNull(map)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<TEntity?> GetSingleAsync(QueryOptions<TEntity> options, CancellationToken cancellationToken = default) =>
        await Query(options).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<PagedResult<TEntity>> GetPagedAsync(PageRequest page, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        NotNull(page);
        var query = Query(options);
        var total = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var items = await Ordered(query, options).Skip(page.Skip).Take(page.Take).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<TEntity>(items, total, page.PageIndex, page.PageSize);
    }

    /// <inheritdoc />
    public async Task<PagedResult<TResult>> GetPagedAsync<TResult>(PageRequest page, Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        NotNull(page);
        var query = Query(options);
        var total = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var items = await Ordered(query, options).Skip(page.Skip).Take(page.Take).Select(NotNull(map)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<TResult>(items, total, page.PageIndex, page.PageSize);
    }

    /// <inheritdoc />
    public Task<long> CountAsync(QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        Query(options).LongCountAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> AnyAsync(QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        Query(options).AnyAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<bool> AllAsync(Expression<Func<TEntity, bool>> predicate, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        !await Query(options).AnyAsync(Negate(NotNull(predicate)), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public Task<int> SumAsync(Expression<Func<TEntity, int>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        Query(options).SumAsync(NotNull(selector), cancellationToken);

    /// <inheritdoc />
    public Task<int?> SumAsync(Expression<Func<TEntity, int?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        Query(options).SumAsync(NotNull(selector), cancellationToken);

    /// <inheritdoc />
    public Task<long> SumAsync(Expression<Func<TEntity, long>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        Query(options).SumAsync(NotNull(selector), cancellationToken);

    /// <inheritdoc />
    public Task<long?> SumAsync(Expression<Func<TEntity, long?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        Query(options).SumAsync(NotNull(selector), cancellationToken);

    /// <inheritdoc />
    public Task<double> SumAsync(Expression<Func<TEntity, double>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        Query(options).SumAsync(NotNull(selector), cancellationToken);

    /// <inheritdoc />
    public Task<double?> SumAsync(Expression<Func<TEntity, double?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        Query(options).SumAsync(NotNull(selector), cancellationToken);

    /// <inheritdoc />
    public Task<float> SumAsync(Expression<Func<TEntity, float>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        Query(options).SumAsync(NotNull(selector), cancellationToken);

    /// <inheritdoc />
    public Task<float?> SumAsync(Expression<Func<TEntity, float?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        Query(options).SumAsync(NotNull(selector), cancellationToken);

    /// <inheritdoc />
    public Task<decimal> SumAsync(Expression<Func<TEntity, decimal>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        Query(options).SumAsync(NotNull(selector), cancellationToken);

    /// <inheritdoc />
    public Task<decimal?> SumAsync(Expression<Func<TEntity, decimal?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        Query(options).SumAsync(NotNull(selector), cancellationToken);

    // ---------------------------------------------------------------- explain

    /// <inheritdoc />
    public QueryPlan Explain(QueryOptions<TEntity>? options = null)
    {
        var notes = new List<string>
        {
            "The driver translates the LINQ query to an aggregation pipeline; filtering, sorting and projections run server-side.",
        };
        if (options is { IncludePaths.Count: > 0 })
        {
            notes.Add($"Includes ({string.Join(", ", options.IncludePaths)}) run as server-side $lookup joins.");
        }

        // The driver renders the shaped queryable as its aggregate pipeline; values are inlined in the rendering.
        var statement = Query(options).ToString() ?? string.Empty;
        return new QueryPlan("MongoDb", statement, [], residual: null,
            serverSideFiltering: false, clientEvaluation: false, partitionScoped: false, notes);
    }

    // ---------------------------------------------------------------- dispose

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases resources. The repository does not own the unit of work, so this is a no-op by default.</summary>
    /// <param name="disposing">Whether the call comes from <see cref="Dispose()" />.</param>
    protected virtual void Dispose(bool disposing)
    {
    }

    // ---------------------------------------------------------------- query plumbing

    /// <summary>Builds the shaped query for the supplied options, optionally with an extra transform.</summary>
    /// <param name="options">The query options, or <c>null</c>.</param>
    /// <param name="extra">An optional extra transform (e.g. an id or ad-hoc filter).</param>
    /// <returns>The shaped query.</returns>
    protected IQueryable<TEntity> Query(QueryOptions<TEntity>? options, Func<IQueryable<TEntity>, IQueryable<TEntity>>? extra = null)
    {
        var query = Collection.AsQueryable().ApplyQueryOptions(options);
        if (extra is not null)
        {
            query = extra(query);
        }

        // Include navigations as a server-side $lookup after the filter, so the join runs over the matched documents.
        if (options is { IncludePaths.Count: > 0 })
        {
            query = MongoInclude.Apply(query, UnitOfWork, options.IncludePaths);
        }

        return query;
    }

    private IQueryable<TEntity> Ordered(IQueryable<TEntity> query, QueryOptions<TEntity>? options)
    {
        if ((options?.Sortings.Count ?? 0) > 0)
        {
            return query;
        }

        var parameter = Expression.Parameter(typeof(TEntity), "e");
        var selector = Expression.Lambda(Expression.MakeMemberAccess(parameter, _idMember), parameter);
        var call = Expression.Call(
            typeof(Queryable),
            nameof(Queryable.OrderBy),
            [typeof(TEntity), _idType],
            query.Expression,
            Expression.Quote(selector));
        return query.Provider.CreateQuery<TEntity>(call);
    }

    private Expression<Func<TEntity, bool>> IdPredicate(TKey id)
    {
        var parameter = Expression.Parameter(typeof(TEntity), "e");
        var body = Expression.Equal(
            Expression.MakeMemberAccess(parameter, _idMember),
            Expression.Constant(id, _idType));
        return Expression.Lambda<Func<TEntity, bool>>(body, parameter);
    }

    private static Expression<Func<TEntity, bool>> Negate(Expression<Func<TEntity, bool>> predicate) =>
        Expression.Lambda<Func<TEntity, bool>>(Expression.Not(predicate.Body), predicate.Parameters);

    private static T NotNull<T>(T value) where T : class =>
        value ?? throw new ArgumentNullException(typeof(T).Name);
}
