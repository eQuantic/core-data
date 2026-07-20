using System.Linq.Expressions;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Read;
using eQuantic.Linq.Expressions;
using eQuantic.Linq.Specification;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;

namespace eQuantic.Core.Data.CosmosDb.Repository;

/// <summary>
///     The native Azure Cosmos DB read repository — synchronous and asynchronous. Reads shape the container's
///     LINQ queryable (translated by the SDK to SQL) with a single <see cref="QueryOptions{TEntity}" />.
///     Synchronous reads execute through the LINQ provider; asynchronous reads stream results through a
///     <c>FeedIterator</c>, with server-side counting.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TKey">The key type.</typeparam>
public abstract class CosmosReadRepository<TEntity, TKey> :
    IQueryableReadRepository<TEntity, TKey>,
    IAsyncQueryableReadRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
{
    /// <summary>The unit of work backing this repository.</summary>
    protected readonly CosmosUnitOfWork UnitOfWork;

    /// <summary>The entity's container.</summary>
    protected readonly Container Container;

    private readonly LambdaExpression _idSelector;
    private readonly string _partitionKeyPath;

    /// <summary>Initializes the repository over a unit of work.</summary>
    /// <param name="unitOfWork">The queryable unit of work (a <see cref="CosmosUnitOfWork" />).</param>
    protected CosmosReadRepository(IQueryableUnitOfWork unitOfWork)
    {
        UnitOfWork = unitOfWork as CosmosUnitOfWork
                     ?? throw new ArgumentException($"The unit of work must be a {nameof(CosmosUnitOfWork)}.", nameof(unitOfWork));
        var configuration = UnitOfWork.Configuration<TEntity>();
        Container = UnitOfWork.GetContainer<TEntity>();
        _partitionKeyPath = configuration.PartitionKeyPath;
        _idSelector = MemberPathExtensions.ToSelector<TEntity>("Id");
    }

    // ---------------------------------------------------------------- synchronous reads

    /// <inheritdoc />
    public TEntity? Get(TKey id, QueryOptions<TEntity>? options = null) =>
        Query(options, query => query.Where(IdPredicate(id))).AsEnumerable().FirstOrDefault();

    /// <inheritdoc />
    public IEnumerable<TEntity> GetAll(QueryOptions<TEntity>? options = null) => Query(options).ToList();

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
    public TEntity? GetFirst(QueryOptions<TEntity> options) => Query(options).AsEnumerable().FirstOrDefault();

    /// <inheritdoc />
    public TResult? GetFirstMapped<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity> options) =>
        Query(options).Select(NotNull(map)).AsEnumerable().FirstOrDefault();

    /// <inheritdoc />
    public TEntity? GetSingle(QueryOptions<TEntity> options) => Query(options).AsEnumerable().SingleOrDefault();

    /// <inheritdoc />
    public PagedResult<TEntity> GetPaged(PageRequest page, QueryOptions<TEntity>? options = null) =>
        GetPagedAsync(page, options).GetAwaiter().GetResult();

    /// <inheritdoc />
    public PagedResult<TResult> GetPaged<TResult>(PageRequest page, Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null) =>
        GetPagedAsync(page, map, options).GetAwaiter().GetResult();

    /// <inheritdoc />
    public long Count(QueryOptions<TEntity>? options = null) => CountAsync(options).GetAwaiter().GetResult();

    /// <inheritdoc />
    public bool Any(QueryOptions<TEntity>? options = null) => Query(options).AsEnumerable().Any();

    /// <inheritdoc />
    public bool All(Expression<Func<TEntity, bool>> predicate, QueryOptions<TEntity>? options = null) =>
        !Query(options).Where(Negate(NotNull(predicate))).AsEnumerable().Any();

    /// <inheritdoc />
    public int Sum(Expression<Func<TEntity, int>> selector, QueryOptions<TEntity>? options = null) => Query(options).Select(NotNull(selector)).AsEnumerable().Sum();

    /// <inheritdoc />
    public int? Sum(Expression<Func<TEntity, int?>> selector, QueryOptions<TEntity>? options = null) => Query(options).Select(NotNull(selector)).AsEnumerable().Sum();

    /// <inheritdoc />
    public long Sum(Expression<Func<TEntity, long>> selector, QueryOptions<TEntity>? options = null) => Query(options).Select(NotNull(selector)).AsEnumerable().Sum();

    /// <inheritdoc />
    public long? Sum(Expression<Func<TEntity, long?>> selector, QueryOptions<TEntity>? options = null) => Query(options).Select(NotNull(selector)).AsEnumerable().Sum();

    /// <inheritdoc />
    public double Sum(Expression<Func<TEntity, double>> selector, QueryOptions<TEntity>? options = null) => Query(options).Select(NotNull(selector)).AsEnumerable().Sum();

    /// <inheritdoc />
    public double? Sum(Expression<Func<TEntity, double?>> selector, QueryOptions<TEntity>? options = null) => Query(options).Select(NotNull(selector)).AsEnumerable().Sum();

    /// <inheritdoc />
    public float Sum(Expression<Func<TEntity, float>> selector, QueryOptions<TEntity>? options = null) => Query(options).Select(NotNull(selector)).AsEnumerable().Sum();

    /// <inheritdoc />
    public float? Sum(Expression<Func<TEntity, float?>> selector, QueryOptions<TEntity>? options = null) => Query(options).Select(NotNull(selector)).AsEnumerable().Sum();

    /// <inheritdoc />
    public decimal Sum(Expression<Func<TEntity, decimal>> selector, QueryOptions<TEntity>? options = null) => Query(options).Select(NotNull(selector)).AsEnumerable().Sum();

    /// <inheritdoc />
    public decimal? Sum(Expression<Func<TEntity, decimal?>> selector, QueryOptions<TEntity>? options = null) => Query(options).Select(NotNull(selector)).AsEnumerable().Sum();

    // ---------------------------------------------------------------- asynchronous reads

    /// <inheritdoc />
    public async Task<TEntity?> GetAsync(TKey id, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        (await MaterializeAsync(Query(options, query => query.Where(IdPredicate(id))).Take(1), cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    /// <inheritdoc />
    public async Task<IEnumerable<TEntity>> GetAllAsync(QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await MaterializeAsync(Query(options), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IEnumerable<TEntity>> GetFilteredAsync(Expression<Func<TEntity, bool>> filter, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await MaterializeAsync(Query(options, query => query.Where(NotNull(filter))), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IEnumerable<TEntity>> AllMatchingAsync(ISpecification<TEntity> specification, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await MaterializeAsync(Query(options, query => query.Where(NotNull(specification).SatisfiedBy())), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IEnumerable<TResult>> GetMappedAsync<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await MaterializeAsync(Query(options).Select(NotNull(map)), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<TEntity?> GetFirstAsync(QueryOptions<TEntity> options, CancellationToken cancellationToken = default) =>
        (await MaterializeAsync(Query(options).Take(1), cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    /// <inheritdoc />
    public async Task<TResult?> GetFirstMappedAsync<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity> options, CancellationToken cancellationToken = default) =>
        (await MaterializeAsync(Query(options).Select(NotNull(map)).Take(1), cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    /// <inheritdoc />
    public async Task<TEntity?> GetSingleAsync(QueryOptions<TEntity> options, CancellationToken cancellationToken = default) =>
        (await MaterializeAsync(Query(options).Take(2), cancellationToken).ConfigureAwait(false)).SingleOrDefault();

    /// <inheritdoc />
    public async Task<PagedResult<TEntity>> GetPagedAsync(PageRequest page, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        NotNull(page);
        var total = await CountAsync(options, cancellationToken).ConfigureAwait(false);
        var items = await MaterializeAsync(Ordered(Query(options), options).Skip(page.Skip).Take(page.Take), cancellationToken).ConfigureAwait(false);
        return new PagedResult<TEntity>(items, total, page.PageIndex, page.PageSize);
    }

    /// <inheritdoc />
    public async Task<PagedResult<TResult>> GetPagedAsync<TResult>(PageRequest page, Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        NotNull(page);
        var total = await CountAsync(options, cancellationToken).ConfigureAwait(false);
        var items = await MaterializeAsync(Ordered(Query(options), options).Skip(page.Skip).Take(page.Take).Select(NotNull(map)), cancellationToken).ConfigureAwait(false);
        return new PagedResult<TResult>(items, total, page.PageIndex, page.PageSize);
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await Query(options).CountAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<bool> AnyAsync(QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        (await MaterializeAsync(Query(options).Take(1), cancellationToken).ConfigureAwait(false)).Count > 0;

    /// <inheritdoc />
    public async Task<bool> AllAsync(Expression<Func<TEntity, bool>> predicate, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        (await MaterializeAsync(Query(options).Where(Negate(NotNull(predicate))).Take(1), cancellationToken).ConfigureAwait(false)).Count == 0;

    /// <inheritdoc />
    public async Task<int> SumAsync(Expression<Func<TEntity, int>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        (await MaterializeAsync(Query(options).Select(NotNull(selector)), cancellationToken).ConfigureAwait(false)).Sum();

    /// <inheritdoc />
    public async Task<int?> SumAsync(Expression<Func<TEntity, int?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        (await MaterializeAsync(Query(options).Select(NotNull(selector)), cancellationToken).ConfigureAwait(false)).Sum();

    /// <inheritdoc />
    public async Task<long> SumAsync(Expression<Func<TEntity, long>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        (await MaterializeAsync(Query(options).Select(NotNull(selector)), cancellationToken).ConfigureAwait(false)).Sum();

    /// <inheritdoc />
    public async Task<long?> SumAsync(Expression<Func<TEntity, long?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        (await MaterializeAsync(Query(options).Select(NotNull(selector)), cancellationToken).ConfigureAwait(false)).Sum();

    /// <inheritdoc />
    public async Task<double> SumAsync(Expression<Func<TEntity, double>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        (await MaterializeAsync(Query(options).Select(NotNull(selector)), cancellationToken).ConfigureAwait(false)).Sum();

    /// <inheritdoc />
    public async Task<double?> SumAsync(Expression<Func<TEntity, double?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        (await MaterializeAsync(Query(options).Select(NotNull(selector)), cancellationToken).ConfigureAwait(false)).Sum();

    /// <inheritdoc />
    public async Task<float> SumAsync(Expression<Func<TEntity, float>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        (await MaterializeAsync(Query(options).Select(NotNull(selector)), cancellationToken).ConfigureAwait(false)).Sum();

    /// <inheritdoc />
    public async Task<float?> SumAsync(Expression<Func<TEntity, float?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        (await MaterializeAsync(Query(options).Select(NotNull(selector)), cancellationToken).ConfigureAwait(false)).Sum();

    /// <inheritdoc />
    public async Task<decimal> SumAsync(Expression<Func<TEntity, decimal>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        (await MaterializeAsync(Query(options).Select(NotNull(selector)), cancellationToken).ConfigureAwait(false)).Sum();

    /// <inheritdoc />
    public async Task<decimal?> SumAsync(Expression<Func<TEntity, decimal?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        (await MaterializeAsync(Query(options).Select(NotNull(selector)), cancellationToken).ConfigureAwait(false)).Sum();

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
        var query = Container
            .GetItemLinqQueryable<TEntity>(allowSynchronousQueryExecution: true, requestOptions: RequestOptions(options))
            .ApplyQueryOptions(options);
        return extra is null ? query : extra(query);
    }

    /// <summary>
    ///     Scopes the query to a single partition when the filter pins the partition key, otherwise lets it run
    ///     cross-partition. Automatic — a filter such as <c>x =&gt; x.Category == "Books"</c> already scopes it.
    /// </summary>
    private QueryRequestOptions? RequestOptions(QueryOptions<TEntity>? options)
    {
        if (options is null)
        {
            return null;
        }

        var partitionKey = CosmosPartitionKeyInference.Infer(_partitionKeyPath, options.Filter, options.Specification?.SatisfiedBy());
        return partitionKey is null ? null : new QueryRequestOptions { PartitionKey = partitionKey };
    }

    private IQueryable<TEntity> Ordered(IQueryable<TEntity> query, QueryOptions<TEntity>? options)
    {
        if ((options?.Sortings.Count ?? 0) > 0)
        {
            return query;
        }

        var call = Expression.Call(
            typeof(Queryable),
            nameof(Queryable.OrderBy),
            [typeof(TEntity), _idSelector.Body.Type],
            query.Expression,
            Expression.Quote(_idSelector));
        return query.Provider.CreateQuery<TEntity>(call);
    }

    private Expression<Func<TEntity, bool>> IdPredicate(TKey id)
    {
        var body = Expression.Equal(_idSelector.Body, Expression.Constant(id, _idSelector.Body.Type));
        return Expression.Lambda<Func<TEntity, bool>>(body, _idSelector.Parameters);
    }

    private static async Task<List<T>> MaterializeAsync<T>(IQueryable<T> query, CancellationToken cancellationToken)
    {
        var results = new List<T>();
        using var iterator = query.ToFeedIterator();
        while (iterator.HasMoreResults)
        {
            results.AddRange(await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private static Expression<Func<TEntity, bool>> Negate(Expression<Func<TEntity, bool>> predicate) =>
        Expression.Lambda<Func<TEntity, bool>>(Expression.Not(predicate.Body), predicate.Parameters);

    private static T NotNull<T>(T value) where T : class => value ?? throw new ArgumentNullException(typeof(T).Name);
}
