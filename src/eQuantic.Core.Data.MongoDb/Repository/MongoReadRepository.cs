using System.Linq.Expressions;
using System.Reflection;
using eQuantic.Core.Data.Query;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Read;
using eQuantic.Linq.Specification;
using MongoDB.Bson;
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
    IGroupedReadRepository<TEntity>,
    IDistinctReadRepository<TEntity>
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
        Windowed(options, 1, query => query.Where(IdPredicate(id))).SingleOrDefault();

    /// <inheritdoc />
    public IEnumerable<TEntity> GetAll(QueryOptions<TEntity>? options = null) =>
        Windowed(options, null).ToList();

    /// <inheritdoc />
    public IEnumerable<TEntity> GetFiltered(Expression<Func<TEntity, bool>> filter, QueryOptions<TEntity>? options = null) =>
        Windowed(options, null, query => query.Where(NotNull(filter))).ToList();

    /// <inheritdoc />
    public IEnumerable<TEntity> AllMatching(ISpecification<TEntity> specification, QueryOptions<TEntity>? options = null) =>
        Windowed(options, null, query => query.Where(NotNull(specification).SatisfiedBy())).ToList();

    /// <inheritdoc />
    public IEnumerable<TResult> GetMapped<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null) =>
        Project(Windowed(options, null), NotNull(map)).ToList();

    /// <inheritdoc />
    public TEntity? GetFirst(QueryOptions<TEntity> options) =>
        Windowed(options, 1).FirstOrDefault();

    /// <inheritdoc />
    public TResult? GetFirstMapped<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity> options) =>
        Project(Windowed(options, 1), NotNull(map)).FirstOrDefault();

    /// <inheritdoc />
    public TEntity? GetSingle(QueryOptions<TEntity> options) =>
        Windowed(options, 2).SingleOrDefault();

    /// <inheritdoc />
    public PagedResult<TEntity> GetPaged(PageRequest page, QueryOptions<TEntity>? options = null)
    {
        NotNull(page);
        ReadShapes.RefuseRowWindow(options, "GetPaged");
        var query = Query(options);
        var total = query.LongCount();
        var items = Ordered(query, options).Skip(page.Skip).Take(page.Take).ToList();
        return new PagedResult<TEntity>(items, total, page.PageIndex, page.PageSize);
    }

    /// <inheritdoc />
    public PagedResult<TResult> GetPaged<TResult>(PageRequest page, Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null)
    {
        NotNull(page);
        ReadShapes.RefuseRowWindow(options, "GetPaged");
        var query = Query(options);
        var total = query.LongCount();
        var items = Project(Ordered(query, options).Skip(page.Skip).Take(page.Take), NotNull(map)).ToList();
        return new PagedResult<TResult>(items, total, page.PageIndex, page.PageSize);
    }

    /// <inheritdoc />
    public long Count(QueryOptions<TEntity>? options = null) => Whole(options, "Count").LongCount();

    /// <inheritdoc />
    public bool Any(QueryOptions<TEntity>? options = null) => Whole(options, "Any").Any();

    /// <inheritdoc />
    public bool All(Expression<Func<TEntity, bool>> predicate, QueryOptions<TEntity>? options = null) =>
        !Whole(options, "All", query => query.Where(Negate(NotNull(predicate)))).Any();

    /// <inheritdoc />
    public int Sum(Expression<Func<TEntity, int>> selector, QueryOptions<TEntity>? options = null) => Whole(options, "Sum").Sum(Summed(selector, "Sum"));

    /// <inheritdoc />
    public int? Sum(Expression<Func<TEntity, int?>> selector, QueryOptions<TEntity>? options = null) => Whole(options, "Sum").Sum(Summed(selector, "Sum"));

    /// <inheritdoc />
    public long Sum(Expression<Func<TEntity, long>> selector, QueryOptions<TEntity>? options = null) => Whole(options, "Sum").Sum(Summed(selector, "Sum"));

    /// <inheritdoc />
    public long? Sum(Expression<Func<TEntity, long?>> selector, QueryOptions<TEntity>? options = null) => Whole(options, "Sum").Sum(Summed(selector, "Sum"));

    /// <inheritdoc />
    public double Sum(Expression<Func<TEntity, double>> selector, QueryOptions<TEntity>? options = null) => Whole(options, "Sum").Sum(Summed(selector, "Sum"));

    /// <inheritdoc />
    public double? Sum(Expression<Func<TEntity, double?>> selector, QueryOptions<TEntity>? options = null) => Whole(options, "Sum").Sum(Summed(selector, "Sum"));

    /// <inheritdoc />
    public float Sum(Expression<Func<TEntity, float>> selector, QueryOptions<TEntity>? options = null) => Whole(options, "Sum").Sum(Summed(selector, "Sum"));

    /// <inheritdoc />
    public float? Sum(Expression<Func<TEntity, float?>> selector, QueryOptions<TEntity>? options = null) => Whole(options, "Sum").Sum(Summed(selector, "Sum"));

    /// <inheritdoc />
    public decimal Sum(Expression<Func<TEntity, decimal>> selector, QueryOptions<TEntity>? options = null) => Whole(options, "Sum").Sum(Summed(selector, "Sum"));

    /// <inheritdoc />
    public decimal? Sum(Expression<Func<TEntity, decimal?>> selector, QueryOptions<TEntity>? options = null) => Whole(options, "Sum").Sum(Summed(selector, "Sum"));

    // ---------------------------------------------------------------- asynchronous reads

    /// <inheritdoc />
    public async Task<TEntity?> GetAsync(TKey id, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await WindowedAsync(options, 1, cancellationToken, query => query.Where(IdPredicate(id))).ConfigureAwait(false))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IEnumerable<TEntity>> GetAllAsync(QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await WindowedAsync(options, null, cancellationToken).ConfigureAwait(false)).ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IEnumerable<TEntity>> GetFilteredAsync(Expression<Func<TEntity, bool>> filter, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await WindowedAsync(options, null, cancellationToken, query => query.Where(NotNull(filter))).ConfigureAwait(false))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IEnumerable<TEntity>> AllMatchingAsync(ISpecification<TEntity> specification, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await WindowedAsync(options, null, cancellationToken, query => query.Where(NotNull(specification).SatisfiedBy())).ConfigureAwait(false))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    ///     A relation count in the map (<c>x.Versions.Count(v =&gt; v.DeletedAt == null)</c>) runs as a <c>$lookup</c>
    ///     whose pipeline matches the related documents and counts them (<c>$count</c>): the server counts, and no
    ///     related document travels.
    /// </remarks>
    public async Task<IEnumerable<TResult>> GetMappedAsync<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await ProjectAsync(await WindowedAsync(options, null, cancellationToken).ConfigureAwait(false), NotNull(map), false, cancellationToken)
                .ConfigureAwait(false))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<TEntity?> GetFirstAsync(QueryOptions<TEntity> options, CancellationToken cancellationToken = default) =>
        await (await WindowedAsync(options, 1, cancellationToken).ConfigureAwait(false)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<TResult?> GetFirstMappedAsync<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity> options, CancellationToken cancellationToken = default) =>
        await (await ProjectAsync(await WindowedAsync(options, 1, cancellationToken).ConfigureAwait(false), NotNull(map), false, cancellationToken)
                .ConfigureAwait(false))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<TEntity?> GetSingleAsync(QueryOptions<TEntity> options, CancellationToken cancellationToken = default) =>
        await (await WindowedAsync(options, 2, cancellationToken).ConfigureAwait(false)).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<PagedResult<TEntity>> GetPagedAsync(PageRequest page, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        NotNull(page);
        ReadShapes.RefuseRowWindow(options, "GetPagedAsync");
        var query = await QueryAsync(options, cancellationToken: cancellationToken).ConfigureAwait(false);
        var total = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var items = await Ordered(query, options).Skip(page.Skip).Take(page.Take).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<TEntity>(items, total, page.PageIndex, page.PageSize);
    }

    /// <inheritdoc />
    public async Task<PagedResult<TResult>> GetPagedAsync<TResult>(PageRequest page, Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        NotNull(page);
        ReadShapes.RefuseRowWindow(options, "GetPagedAsync");
        var query = await QueryAsync(options, cancellationToken: cancellationToken).ConfigureAwait(false);
        var total = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var items = await (await ProjectAsync(Ordered(query, options).Skip(page.Skip).Take(page.Take), NotNull(map), false, cancellationToken)
            .ConfigureAwait(false)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<TResult>(items, total, page.PageIndex, page.PageSize);
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await WholeAsync(options, "CountAsync", cancellationToken).ConfigureAwait(false)).LongCountAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<bool> AnyAsync(QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await WholeAsync(options, "AnyAsync", cancellationToken).ConfigureAwait(false)).AnyAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<bool> AllAsync(Expression<Func<TEntity, bool>> predicate, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        !await (await WholeAsync(options, "AllAsync", cancellationToken, query => query.Where(Negate(NotNull(predicate)))).ConfigureAwait(false))
            .AnyAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<int> SumAsync(Expression<Func<TEntity, int>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await WholeAsync(options, "SumAsync", cancellationToken).ConfigureAwait(false)).SumAsync(Summed(selector, "SumAsync"), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<int?> SumAsync(Expression<Func<TEntity, int?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await WholeAsync(options, "SumAsync", cancellationToken).ConfigureAwait(false)).SumAsync(Summed(selector, "SumAsync"), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<long> SumAsync(Expression<Func<TEntity, long>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await WholeAsync(options, "SumAsync", cancellationToken).ConfigureAwait(false)).SumAsync(Summed(selector, "SumAsync"), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<long?> SumAsync(Expression<Func<TEntity, long?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await WholeAsync(options, "SumAsync", cancellationToken).ConfigureAwait(false)).SumAsync(Summed(selector, "SumAsync"), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<double> SumAsync(Expression<Func<TEntity, double>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await WholeAsync(options, "SumAsync", cancellationToken).ConfigureAwait(false)).SumAsync(Summed(selector, "SumAsync"), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<double?> SumAsync(Expression<Func<TEntity, double?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await WholeAsync(options, "SumAsync", cancellationToken).ConfigureAwait(false)).SumAsync(Summed(selector, "SumAsync"), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<float> SumAsync(Expression<Func<TEntity, float>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await WholeAsync(options, "SumAsync", cancellationToken).ConfigureAwait(false)).SumAsync(Summed(selector, "SumAsync"), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<float?> SumAsync(Expression<Func<TEntity, float?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await WholeAsync(options, "SumAsync", cancellationToken).ConfigureAwait(false)).SumAsync(Summed(selector, "SumAsync"), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<decimal> SumAsync(Expression<Func<TEntity, decimal>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await WholeAsync(options, "SumAsync", cancellationToken).ConfigureAwait(false)).SumAsync(Summed(selector, "SumAsync"), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<decimal?> SumAsync(Expression<Func<TEntity, decimal?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await WholeAsync(options, "SumAsync", cancellationToken).ConfigureAwait(false)).SumAsync(Summed(selector, "SumAsync"), cancellationToken).ConfigureAwait(false);

    // ---------------------------------------------------------------- min / max / average

    /// <inheritdoc />
    /// <remarks>
    ///     Runs as a single-group aggregation (<c>$group</c> + <c>$min</c>) on the server; an empty match yields <c>default</c>.
    ///     A member stored as an enum's labels reads the distinct members present (at most the enum's members) and answers
    ///     the one with the smallest value, as C# does, never the smallest label.
    /// </remarks>
    public async Task<TResult?> MinAsync<TResult>(Expression<Func<TEntity, TResult>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        EnumOrder.LabelEnumOf(NotNull(selector), MongoModeling.IsLabelEnum) is { } labels
            ? EnumOrder.FromValue<TResult>(labels, EnumOrder.Extreme(await MembersPresentAsync(selector, options, cancellationToken).ConfigureAwait(false), largest: false))
            : await AggregateAsync<TResult>(selector, nameof(Enumerable.Min), [typeof(TEntity), typeof(TResult)], options, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    ///     Runs as a single-group aggregation (<c>$group</c> + <c>$max</c>) on the server; an empty match yields <c>default</c>.
    ///     A member stored as an enum's labels answers the member with the largest value, as <see cref="MinAsync{TResult}" /> does.
    /// </remarks>
    public async Task<TResult?> MaxAsync<TResult>(Expression<Func<TEntity, TResult>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        EnumOrder.LabelEnumOf(NotNull(selector), MongoModeling.IsLabelEnum) is { } labels
            ? EnumOrder.FromValue<TResult>(labels, EnumOrder.Extreme(await MembersPresentAsync(selector, options, cancellationToken).ConfigureAwait(false), largest: true))
            : await AggregateAsync<TResult>(selector, nameof(Enumerable.Max), [typeof(TEntity), typeof(TResult)], options, cancellationToken).ConfigureAwait(false);

    /// <summary>The distinct members a selector of a member stored as labels reads under the options (at most the enum's members).</summary>
    private async Task<IReadOnlyList<object?>> MembersPresentAsync(LambdaExpression selector, QueryOptions<TEntity>? options,
        CancellationToken cancellationToken)
    {
        var body = selector.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } conversion)
        {
            body = conversion.Operand;
        }

        var query = await WholeAsync(options, "an aggregate", cancellationToken).ConfigureAwait(false);
        return await ((Task<IReadOnlyList<object?>>)typeof(MongoReadRepository<TEntity, TKey>)
                .GetMethod(nameof(DistinctOfAsync), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(body.Type)
                .Invoke(null, [query, Expression.Lambda(body, selector.Parameters), cancellationToken])!)
            .ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<object?>> DistinctOfAsync<TMember>(IQueryable<TEntity> query, LambdaExpression member,
        CancellationToken cancellationToken) =>
        (await query.Select((Expression<Func<TEntity, TMember>>)member).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false))
        .Cast<object?>().ToList();

    /// <summary>A sum's selector, refused over a member stored as an enum's labels (a label has no arithmetic).</summary>
    private static Expression<Func<TEntity, TSum>> Summed<TSum>(Expression<Func<TEntity, TSum>> selector, string aggregate) =>
        EnumOrder.LabelEnumOf(NotNull(selector), MongoModeling.IsLabelEnum) is { } labels
            ? throw EnumOrder.ArithmeticRefusal(labels, aggregate)
            : selector;

    /// <inheritdoc />
    /// <remarks>
    ///     Runs as a single-group aggregation (<c>$group</c> + <c>$avg</c>), the member converted to double on the server; an
    ///     empty match yields <c>0</c>. A member stored as an enum's labels is refused: a label has no arithmetic.
    /// </remarks>
    public async Task<double> AverageAsync<TValue>(Expression<Func<TEntity, TValue>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        if (EnumOrder.LabelEnumOf(NotNull(selector), MongoModeling.IsLabelEnum) is { } labels)
        {
            throw EnumOrder.ArithmeticRefusal(labels, "AverageAsync");
        }

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

        return await (await WholeAsync(options, "an aggregate", cancellationToken).ConfigureAwait(false)).GroupBy(_ => 1).Select(aggregate)
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
        var predicate = having is null ? null : GroupInterpreter.InterpretHaving(having, group.Key);
        EnumOrder.RefuseLabelAggregates(group, predicate, member =>
            MemberType(member) is { } type && MongoModeling.IsLabelEnum(type) ? Nullable.GetUnderlyingType(type) ?? type : null);

        var grouped = (await WholeAsync(options, "GroupByAsync", cancellationToken).ConfigureAwait(false)).GroupBy(keySelector);
        if (having is not null)
        {
            // A key holding an enum's labels compares by the members' values, as C# does: an $in over the labels whose
            // member satisfies the comparison, never the labels' text.
            grouped = grouped.Where(EnumOrder.RewriteComparisons(having, MongoModeling.IsLabelEnum));
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

        var after = continuationToken is null ? null : IdAfter(ReadToken(continuationToken));
        var query = await WholeAsync(options, "GetPageAsync", cancellationToken,
            after is null ? null : source => source.Where(after)).ConfigureAwait(false);

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
        var source = (IAsyncCursorSource<TEntity>)await WholeAsync(options, "GetStreamAsync", cancellationToken).ConfigureAwait(false);
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

        if (options is { IncludeShapes.Count: > 0 })
        {
            notes.Add($"Shaped includes ({string.Join(", ", options.IncludeShapes.Keys)}) run their $match, $sort, $skip and $limit " +
                      "inside the $lookup pipeline, per parent.");
        }

        if (options is not null && !MongoStages.NaturalNulls(ReadShapes.Sorts(options)))
        {
            notes.Add("A placement of nulls against MongoDB's order (nulls sort as the smallest value) sorts on an is-null flag first ($addFields + $sort).");
        }

        if (options is not null && ReadShapes.Sorts(options).Any(sort => MongoStages.SortsByLabels(sort.Key)))
        {
            notes.Add("An enum stored as its labels sorts by the value of the member each label stands for ($addFields of a $switch + $sort), as C# orders it.");
        }

        if (options is { SkipCount: not null } or { TakeCount: not null })
        {
            notes.Add("Skip/Take run as $skip/$limit; no count runs.");
        }

        // The driver renders the shaped queryable as its aggregate pipeline; values are inlined in the rendering.
        var (offset, limit) = ReadShapes.Window(options, null);
        var shaped = Shaped(options, null, offset, limit, explaining: true);
        var unresolved = (UnitOfWork.Session is null ? Collection.AsQueryable() : Collection.AsQueryable(UnitOfWork.Session))
            .ApplyQueryOptions(options);
        if (GlobalFilter(options) is { } globalFilter)
        {
            unresolved = unresolved.Where(globalFilter);
        }

        notes.AddRange(MongoPredicates.Notes(unresolved.Expression, UnitOfWork));
        var statement = shaped.ToString() ?? string.Empty;
        return new QueryPlan("MongoDb", statement, [], residual: null,
            serverSideFiltering: false, clientEvaluation: false, partitionScoped: false, notes);
    }

    // ---------------------------------------------------------------- distinct values

    /// <inheritdoc />
    /// <remarks>
    ///     Runs as a <c>$group</c> on the selected value after the read's <c>$match</c> (the driver's LINQ
    ///     <c>Distinct</c>): only the distinct values travel.
    /// </remarks>
    public async Task<IReadOnlyList<TResult>> GetDistinctAsync<TResult>(Expression<Func<TEntity, TResult>> selector,
        Expression<Func<TEntity, bool>>? filter = null, QueryOptions<TEntity>? options = null,
        CancellationToken cancellationToken = default)
    {
        NotNull(selector);
        ReadShapes.RefuseForDistinct(options);
        var query = await QueryAsync(options, filter is null ? null : source => source.Where(filter), cancellationToken).ConfigureAwait(false);
        return await query.Select(selector).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
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
    protected IQueryable<TEntity> Query(QueryOptions<TEntity>? options, Func<IQueryable<TEntity>, IQueryable<TEntity>>? extra = null) =>
        Shaped(options, extra, null, null);

    /// <summary>
    ///     Builds the shaped query for the supplied options, optionally with an extra transform, for an asynchronous read:
    ///     what <see cref="Query" /> builds, the keys a filter across a relation stands for read asynchronously and
    ///     cancelled by <paramref name="cancellationToken" />.
    /// </summary>
    /// <param name="options">The query options, or <c>null</c>.</param>
    /// <param name="extra">An optional extra transform (e.g. an id or ad-hoc filter).</param>
    /// <param name="cancellationToken">Cancels the reads the query needs before it runs.</param>
    /// <returns>The shaped query.</returns>
    protected Task<IQueryable<TEntity>> QueryAsync(QueryOptions<TEntity>? options,
        Func<IQueryable<TEntity>, IQueryable<TEntity>>? extra = null, CancellationToken cancellationToken = default) =>
        ShapedAsync(options, extra, null, null, cancellationToken);

    /// <summary>The shaped query for a read over every matching row, which refuses the options' <c>Skip</c>/<c>Take</c>.</summary>
    private IQueryable<TEntity> Whole(QueryOptions<TEntity>? options, string read,
        Func<IQueryable<TEntity>, IQueryable<TEntity>>? extra = null)
    {
        ReadShapes.RefuseRowWindow(options, read);
        return Query(options, extra);
    }

    /// <summary>What <see cref="Whole" /> builds, for an asynchronous read.</summary>
    private Task<IQueryable<TEntity>> WholeAsync(QueryOptions<TEntity>? options, string read, CancellationToken cancellationToken,
        Func<IQueryable<TEntity>, IQueryable<TEntity>>? extra = null)
    {
        ReadShapes.RefuseRowWindow(options, read);
        return QueryAsync(options, extra, cancellationToken);
    }

    /// <summary>The shaped query for a read returning rows: the options' <c>Skip</c>/<c>Take</c> within the read's own limit.</summary>
    private IQueryable<TEntity> Windowed(QueryOptions<TEntity>? options, int? limit,
        Func<IQueryable<TEntity>, IQueryable<TEntity>>? extra = null)
    {
        var (offset, effective) = ReadShapes.Window(options, limit);
        return Shaped(options, extra, offset, effective);
    }

    /// <summary>What <see cref="Windowed" /> builds, for an asynchronous read.</summary>
    private Task<IQueryable<TEntity>> WindowedAsync(QueryOptions<TEntity>? options, int? limit, CancellationToken cancellationToken,
        Func<IQueryable<TEntity>, IQueryable<TEntity>>? extra = null)
    {
        var (offset, effective) = ReadShapes.Window(options, limit);
        return ShapedAsync(options, extra, offset, effective, cancellationToken);
    }

    /// <summary>
    ///     Applies the map, for a synchronous read: what <see cref="ProjectAsync{TResult}" /> does, the keys a count's
    ///     relations read first read through the driver's synchronous API (every task then completes before it is returned).
    /// </summary>
    private IQueryable<TResult> Project<TResult>(IQueryable<TEntity> query, Expression<Func<TEntity, TResult>> map) =>
        ProjectAsync(query, map, synchronous: true, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    ///     Applies the map — through the driver's LINQ <c>$project</c>, after a <c>$lookup</c> + <c>$count</c> per relation
    ///     count the map holds, whose results ride beside the document into the projection. A count's filter that crosses
    ///     the counted documents' own relations has them resolved first, in the session, as a filter's are.
    /// </summary>
    private async Task<IQueryable<TResult>> ProjectAsync<TResult>(IQueryable<TEntity> query, Expression<Func<TEntity, TResult>> map,
        bool synchronous, CancellationToken cancellationToken)
    {
        if (RelationCountInterpreter.Interpret(map, property => MongoInclude.IsRelation(typeof(TEntity), property)) is not { } counted)
        {
            return query.Select(map);
        }

        var fields = counted.Counts.Select((_, index) => "__eq_count" + index).ToList();
        var relations = new MongoRelations(UnitOfWork, synchronous, cancellationToken);
        var lookups = new List<BsonDocument>(counted.Counts.Count);
        for (var index = 0; index < counted.Counts.Count; index++)
        {
            // One at a time: a session runs one operation at a time.
            lookups.Add(await MongoInclude.CountLookupAsync(UnitOfWork, typeof(TEntity), counted.Counts[index], fields[index], relations)
                .ConfigureAwait(false));
        }

        query = MongoStages.Append(query, lookups);

        var carry = new BsonDocument("$project", new BsonDocument
        {
            { "_id", 0 },
            { "e", "$$ROOT" },
            {
                "c", new BsonArray(fields.Select(field => new BsonDocument("$toLong", new BsonDocument("$ifNull", new BsonArray
                {
                    new BsonDocument("$arrayElemAt", new BsonArray { "$" + field + ".n", 0 }),
                    0,
                }))))
            },
        });
        var carried = query.AppendStage(new BsonDocumentPipelineStageDefinition<TEntity, MongoRelationCounts<TEntity>>(
            carry, MongoRelationCounts<TEntity>.Serializer));
        carried = MongoStages.Append(carried, [new BsonDocument("$unset", new BsonArray(fields.Select(field => "e." + field)))]);

        var carrier = Expression.Parameter(typeof(MongoRelationCounts<TEntity>), "carrier");
        var body = new Substitution(new Dictionary<ParameterExpression, Expression>
        {
            [counted.Projection.Parameters[0]] = Expression.Property(carrier, nameof(MongoRelationCounts<TEntity>.Entity)),
            [counted.Projection.Parameters[1]] = Expression.Property(carrier, nameof(MongoRelationCounts<TEntity>.Counts)),
        }).Visit(counted.Projection.Body);
        return carried.Select(Expression.Lambda<Func<MongoRelationCounts<TEntity>, TResult>>(body, carrier));
    }

    private sealed class Substitution(Dictionary<ParameterExpression, Expression> map) : System.Linq.Expressions.ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            map.TryGetValue(node, out var replacement) ? replacement : node;
    }

    private IQueryable<TEntity> Shaped(QueryOptions<TEntity>? options, Func<IQueryable<TEntity>, IQueryable<TEntity>>? extra,
        int? offset, int? limit, bool explaining = false)
    {
        // The filters as the driver reads them: an array's Contains normalized, text without regard to case as an
        // escaped regular expression, and each test across a relation resolved — the related keys read first, in the
        // session, through the driver's synchronous API, then an $in over them, or a $lookup when they are too many to
        // carry (an explain runs nothing, and shows the keys empty).
        var query = Filtered(options, extra);
        query = explaining
            ? query.Provider.CreateQuery<TEntity>(MongoPredicates.Unresolved(query.Expression))
            : MongoPredicates.Ready(query, UnitOfWork);
        return Finished(query, options, offset, limit);
    }

    /// <summary>What <see cref="Shaped" /> builds, for an asynchronous read: the related keys read asynchronously, with the token.</summary>
    private async Task<IQueryable<TEntity>> ShapedAsync(QueryOptions<TEntity>? options,
        Func<IQueryable<TEntity>, IQueryable<TEntity>>? extra, int? offset, int? limit, CancellationToken cancellationToken)
    {
        var query = await MongoPredicates.ReadyAsync(Filtered(options, extra), UnitOfWork, cancellationToken).ConfigureAwait(false);
        return Finished(query, options, offset, limit);
    }

    /// <summary>The collection's query in the session, shaped by the options, the global filter and the extra transform.</summary>
    private IQueryable<TEntity> Filtered(QueryOptions<TEntity>? options, Func<IQueryable<TEntity>, IQueryable<TEntity>>? extra)
    {
        // Reads join the active transaction session, so a query inside a transaction sees its own writes.
        var session = UnitOfWork.Session;
        var query = (session is null ? Collection.AsQueryable() : Collection.AsQueryable(session)).ApplyQueryOptions(options);
        if (GlobalFilter(options) is { } global)
        {
            query = query.Where(global);
        }

        return extra is null ? query : extra(query);
    }

    /// <summary>The window and the includes, over a query whose filters are ready.</summary>
    private IQueryable<TEntity> Finished(IQueryable<TEntity> query, QueryOptions<TEntity>? options, int? offset, int? limit)
    {
        // The window applies before the includes, so the joins run over the page only; a skip orders by the id when
        // the read has no order of its own, so pages are stable.
        if (offset is not null)
        {
            query = Ordered(query, options).Skip(offset.Value);
        }

        if (limit is not null)
        {
            query = query.Take(limit.Value);
        }

        // Include navigations as a server-side $lookup after the filter, so the join runs over the matched documents.
        if (options is { IncludePaths.Count: > 0 })
        {
            query = MongoInclude.Apply(query, UnitOfWork, options.IncludePaths, options.IncludeShapes);
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

    /// <summary>The type of a member path of the entity (<c>Box.Name</c>), or <c>null</c> when there is none.</summary>
    private static Type? MemberType(string path)
    {
        var type = typeof(TEntity);
        foreach (var segment in path.Split('.'))
        {
            var member = (MemberInfo?)type.GetProperty(segment) ?? type.GetField(segment);
            type = member switch
            {
                PropertyInfo property => property.PropertyType,
                FieldInfo field => field.FieldType,
                _ => null!,
            };
            if (type is null)
            {
                return null;
            }
        }

        return type;
    }

    private static T NotNull<T>(T value) where T : class =>
        value ?? throw new ArgumentNullException(typeof(T).Name);
}
