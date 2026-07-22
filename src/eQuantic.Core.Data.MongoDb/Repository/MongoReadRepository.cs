using System.Linq.Expressions;
using System.Reflection;
using eQuantic.Core.Data.Query;
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
    IExplainableRepository<TEntity>,
    IStreamingReadRepository<TEntity>,
    IContinuationReadRepository<TEntity>,
    IAggregateReadRepository<TEntity>,
    IGroupedReadRepository<TEntity>
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

    // ---------------------------------------------------------------- min / max / average

    /// <inheritdoc />
    /// <remarks>Runs as a single-group aggregation (<c>$group</c> + <c>$min</c>) on the server; an empty match yields <c>default</c>.</remarks>
    public async Task<TResult?> MinAsync<TResult>(Expression<Func<TEntity, TResult>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await AggregateAsync<TResult>(NotNull(selector), nameof(Enumerable.Min), [typeof(TEntity), typeof(TResult)], options, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>Runs as a single-group aggregation (<c>$group</c> + <c>$max</c>) on the server; an empty match yields <c>default</c>.</remarks>
    public async Task<TResult?> MaxAsync<TResult>(Expression<Func<TEntity, TResult>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await AggregateAsync<TResult>(NotNull(selector), nameof(Enumerable.Max), [typeof(TEntity), typeof(TResult)], options, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>Runs as a single-group aggregation (<c>$group</c> + <c>$avg</c>), the member converted to double on the server; an empty match yields <c>0</c>.</remarks>
    public async Task<double> AverageAsync<TValue>(Expression<Func<TEntity, TValue>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        NotNull(selector);
        var cast = Expression.Lambda<Func<TEntity, double>>(
            Expression.Convert(selector.Body, typeof(double)), selector.Parameters);
        return await AggregateAsync<double>(cast, nameof(Enumerable.Average), [typeof(TEntity)], options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Aggregates the whole match as one server-side group: <c>GroupBy(_ =&gt; 1).Select(g =&gt; g.Min(selector))</c>.</summary>
    private async Task<TAggregated?> AggregateAsync<TAggregated>(LambdaExpression selector, string method,
        Type[] typeArguments, QueryOptions<TEntity>? options, CancellationToken cancellationToken)
    {
        var grouping = Expression.Parameter(typeof(IGrouping<int, TEntity>), "g");
        var aggregate = Expression.Lambda<Func<IGrouping<int, TEntity>, TAggregated>>(
            Expression.Call(typeof(Enumerable), method, typeArguments, grouping, selector), grouping);

        return await Query(options).GroupBy(_ => 1).Select(aggregate)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- grouped reads

    /// <inheritdoc />
    /// <remarks>
    ///     Runs as a server-side <c>$group</c> — the filter applies before grouping, the <paramref name="having" />
    ///     predicate as a <c>$match</c> over the groups, and only the grouped rows travel. The projection is
    ///     validated against the same shapes every provider accepts, so a grouped read ports across stores.
    ///     Group order is store-defined; order the result client-side.
    /// </remarks>
    public async Task<IReadOnlyList<TResult>> GroupByAsync<TGroup, TResult>(
        Expression<Func<TEntity, TGroup>> keySelector,
        Expression<Func<IGrouping<TGroup, TEntity>, TResult>> resultSelector,
        Expression<Func<IGrouping<TGroup, TEntity>, bool>>? having = null,
        QueryOptions<TEntity>? options = null,
        CancellationToken cancellationToken = default)
    {
        if (options is { Sortings.Count: > 0 })
        {
            throw new NotSupportedException("Sorting does not apply to a grouped read; order the grouped result client-side.");
        }

        if (options is { IncludePaths.Count: > 0 })
        {
            throw new NotSupportedException("Include does not apply to a grouped projection; drop the includes.");
        }

        // Interpret first so the contract is uniform across providers: the same shapes, the same rejections.
        var group = GroupInterpreter.Interpret(NotNull(keySelector), NotNull(resultSelector));
        if (having is not null)
        {
            GroupInterpreter.InterpretHaving(having, group.Key);
        }

        var grouped = Query(options).GroupBy(keySelector);
        if (having is not null)
        {
            grouped = grouped.Where(having);
        }

        return await grouped.Select(resultSelector).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- continuation paging (keyset by id)

    /// <inheritdoc />
    /// <remarks>
    ///     MongoDB has no server continuation token for queries, so this pages by <b>keyset</b>: ordered by the id,
    ///     each page filters <c>id &gt; last</c> — both evaluated server-side under the same BSON ordering, so deep
    ///     pages cost one index seek instead of a skip. Custom sortings are not supported (the keyset is the id).
    /// </remarks>
    public async Task<ContinuedResult<TEntity>> GetPageAsync(int pageSize, string? continuationToken = null,
        QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "The page size must be at least 1.");
        }

        if (options is { Sortings.Count: > 0 })
        {
            throw new NotSupportedException(
                "Keyset paging orders by the id; custom sortings are not supported — use GetPagedAsync, or sort each page client-side.");
        }

        var query = Query(options);
        if (continuationToken is not null)
        {
            query = query.Where(IdAfter(ReadToken(continuationToken)));
        }

        var items = await Ordered(query, options).Take(pageSize).ToListAsync(cancellationToken).ConfigureAwait(false);
        var token = items.Count < pageSize ? null : WriteToken(IdValue(items[^1]));
        return new ContinuedResult<TEntity>(items, token);
    }

    private object? IdValue(TEntity entity) => _idMember is PropertyInfo property
        ? property.GetValue(entity)
        : ((FieldInfo)_idMember).GetValue(entity);

    private string WriteToken(object? id) => id switch
    {
        null => throw new InvalidOperationException($"'{typeof(TEntity).Name}' produced a null id; keyset paging needs one."),
        MongoDB.Bson.ObjectId objectId => objectId.ToString(),
        _ => System.Text.Json.JsonSerializer.Serialize(id, _idType),
    };

    private object? ReadToken(string token) => _idType == typeof(MongoDB.Bson.ObjectId)
        ? MongoDB.Bson.ObjectId.Parse(token)
        : System.Text.Json.JsonSerializer.Deserialize(token, _idType);

    /// <summary>Builds <c>e =&gt; e.Id &gt; id</c> (or the <c>CompareTo</c> form for types without the operator).</summary>
    private Expression<Func<TEntity, bool>> IdAfter(object? id)
    {
        var parameter = Expression.Parameter(typeof(TEntity), "e");
        var member = Expression.MakeMemberAccess(parameter, _idMember);
        var value = Expression.Constant(id, _idType);

        Expression body;
        try
        {
            body = Expression.GreaterThan(member, value);
        }
        catch (InvalidOperationException)
        {
            // string/Guid have no '>' operator; the driver translates CompareTo to the same server-side ordering.
            var compare = Expression.Call(member, _idType.GetMethod(nameof(IComparable.CompareTo), [_idType])!, value);
            body = Expression.GreaterThan(compare, Expression.Constant(0));
        }

        return Expression.Lambda<Func<TEntity, bool>>(body, parameter);
    }

    // ---------------------------------------------------------------- streaming

    /// <inheritdoc />
    public async IAsyncEnumerable<TEntity> GetStreamAsync(QueryOptions<TEntity>? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Streams through the driver cursor: one batch in memory at a time.
        var source = (IAsyncCursorSource<TEntity>)Query(options);
        using var cursor = await source.ToCursorAsync(cancellationToken).ConfigureAwait(false);
        while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var entity in cursor.Current)
            {
                yield return entity;
            }
        }
    }

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
        // Reads join the active transaction session, so a query inside a transaction sees its own writes.
        var session = UnitOfWork.Session;
        var query = (session is null ? Collection.AsQueryable() : Collection.AsQueryable(session)).ApplyQueryOptions(options);
        if (GlobalFilter(options) is { } global)
        {
            query = query.Where(global);
        }

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

    /// <summary>The global filter for this entity, unless the options opt out of query filters.</summary>
    private Expression<Func<TEntity, bool>>? GlobalFilter(QueryOptions<TEntity>? options) =>
        options is { IgnoreQueryFilters: true } ? null : UnitOfWork.GlobalFilter<TEntity>();

    private static Expression<Func<TEntity, bool>> Negate(Expression<Func<TEntity, bool>> predicate) =>
        Expression.Lambda<Func<TEntity, bool>>(Expression.Not(predicate.Body), predicate.Parameters);

    private static T NotNull<T>(T value) where T : class =>
        value ?? throw new ArgumentNullException(typeof(T).Name);
}
