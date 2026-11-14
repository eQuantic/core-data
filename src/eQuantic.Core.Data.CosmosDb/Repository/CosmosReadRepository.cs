using System.Linq.Expressions;
using System.Reflection;
using eQuantic.Core.Data.Query;
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
    IAsyncQueryableReadRepository<TEntity, TKey>,
    IExplainableRepository<TEntity>,
    IContinuationReadRepository<TEntity>,
    IStreamingReadRepository<TEntity>,
    IAggregateReadRepository<TEntity>,
    IGroupedReadRepository<TEntity>,
    IDistinctReadRepository<TEntity>
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

        // Partition inference pins single-path keys only; a hierarchical key's queries fan out (the inference
        // guard sees the '/' and declines), so the sentinel keeps the path harmlessly un-inferable.
        _partitionKeyPath = configuration.HasHierarchicalPartitionKey
            ? string.Join("/", configuration.PartitionKeyPaths)
            : configuration.PartitionKeyPaths[0];
        _idSelector = MemberPathExtensions.ToSelector<TEntity>("Id");
    }

    // ---------------------------------------------------------------- synchronous reads

    /// <inheritdoc />
    public TEntity? Get(TKey id, QueryOptions<TEntity>? options = null) => GetAsync(id, options).GetAwaiter().GetResult();

    /// <inheritdoc />
    public IEnumerable<TEntity> GetAll(QueryOptions<TEntity>? options = null) => GetAllAsync(options).GetAwaiter().GetResult();

    /// <inheritdoc />
    public IEnumerable<TEntity> GetFiltered(Expression<Func<TEntity, bool>> filter, QueryOptions<TEntity>? options = null) =>
        GetFilteredAsync(filter, options).GetAwaiter().GetResult();

    /// <inheritdoc />
    public IEnumerable<TEntity> AllMatching(ISpecification<TEntity> specification, QueryOptions<TEntity>? options = null) =>
        AllMatchingAsync(specification, options).GetAwaiter().GetResult();

    /// <inheritdoc />
    public IEnumerable<TResult> GetMapped<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null) =>
        GetMappedAsync(map, options).GetAwaiter().GetResult();

    /// <inheritdoc />
    public TEntity? GetFirst(QueryOptions<TEntity> options) => GetFirstAsync(options).GetAwaiter().GetResult();

    /// <inheritdoc />
    public TResult? GetFirstMapped<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity> options) =>
        GetFirstMappedAsync(map, options).GetAwaiter().GetResult();

    /// <inheritdoc />
    public TEntity? GetSingle(QueryOptions<TEntity> options) => GetSingleAsync(options).GetAwaiter().GetResult();

    /// <inheritdoc />
    public PagedResult<TEntity> GetPaged(PageRequest page, QueryOptions<TEntity>? options = null) =>
        GetPagedAsync(page, options).GetAwaiter().GetResult();

    /// <inheritdoc />
    public PagedResult<TResult> GetPaged<TResult>(PageRequest page, Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null) =>
        GetPagedAsync(page, map, options).GetAwaiter().GetResult();

    /// <inheritdoc />
    public long Count(QueryOptions<TEntity>? options = null) => CountAsync(options).GetAwaiter().GetResult();

    /// <inheritdoc />
    public bool Any(QueryOptions<TEntity>? options = null) => AnyAsync(options).GetAwaiter().GetResult();

    /// <inheritdoc />
    public bool All(Expression<Func<TEntity, bool>> predicate, QueryOptions<TEntity>? options = null) =>
        AllAsync(predicate, options).GetAwaiter().GetResult();

    /// <inheritdoc />
    public int Sum(Expression<Func<TEntity, int>> selector, QueryOptions<TEntity>? options = null) => Ready(Whole(options, "Sum")).Select(Summed(selector)).AsEnumerable().Sum();

    /// <inheritdoc />
    public int? Sum(Expression<Func<TEntity, int?>> selector, QueryOptions<TEntity>? options = null) => Ready(Whole(options, "Sum")).Select(Summed(selector)).AsEnumerable().Sum();

    /// <inheritdoc />
    public long Sum(Expression<Func<TEntity, long>> selector, QueryOptions<TEntity>? options = null) => Ready(Whole(options, "Sum")).Select(Summed(selector)).AsEnumerable().Sum();

    /// <inheritdoc />
    public long? Sum(Expression<Func<TEntity, long?>> selector, QueryOptions<TEntity>? options = null) => Ready(Whole(options, "Sum")).Select(Summed(selector)).AsEnumerable().Sum();

    /// <inheritdoc />
    public double Sum(Expression<Func<TEntity, double>> selector, QueryOptions<TEntity>? options = null) => Ready(Whole(options, "Sum")).Select(Summed(selector)).AsEnumerable().Sum();

    /// <inheritdoc />
    public double? Sum(Expression<Func<TEntity, double?>> selector, QueryOptions<TEntity>? options = null) => Ready(Whole(options, "Sum")).Select(Summed(selector)).AsEnumerable().Sum();

    /// <inheritdoc />
    public float Sum(Expression<Func<TEntity, float>> selector, QueryOptions<TEntity>? options = null) => Ready(Whole(options, "Sum")).Select(Summed(selector)).AsEnumerable().Sum();

    /// <inheritdoc />
    public float? Sum(Expression<Func<TEntity, float?>> selector, QueryOptions<TEntity>? options = null) => Ready(Whole(options, "Sum")).Select(Summed(selector)).AsEnumerable().Sum();

    /// <inheritdoc />
    public decimal Sum(Expression<Func<TEntity, decimal>> selector, QueryOptions<TEntity>? options = null) => Ready(Whole(options, "Sum")).Select(Summed(selector)).AsEnumerable().Sum();

    /// <inheritdoc />
    public decimal? Sum(Expression<Func<TEntity, decimal?>> selector, QueryOptions<TEntity>? options = null) => Ready(Whole(options, "Sum")).Select(Summed(selector)).AsEnumerable().Sum();

    // ---------------------------------------------------------------- asynchronous reads

    /// <inheritdoc />
    public async Task<TEntity?> GetAsync(TKey id, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        (await ReadAsync(options, IdPredicate(id), 1, cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    /// <inheritdoc />
    public async Task<IEnumerable<TEntity>> GetAllAsync(QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await ReadAsync(options, null, null, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IEnumerable<TEntity>> GetFilteredAsync(Expression<Func<TEntity, bool>> filter, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await ReadAsync(options, NotNull(filter), null, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IEnumerable<TEntity>> AllMatchingAsync(ISpecification<TEntity> specification, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await ReadAsync(options, NotNull(specification).SatisfiedBy(), null, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    ///     A relation count in the map (<c>x.Versions.Count(v =&gt; v.DeletedAt == null)</c>) runs as a grouped count over
    ///     the related container (<c>SELECT fk, COUNT(1) … GROUP BY fk</c>) for the parents read; the parents are then
    ///     read whole and the map runs in memory over them and their counts.
    /// </remarks>
    public async Task<IEnumerable<TResult>> GetMappedAsync<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        var (offset, limit) = ReadShapes.Window(options, null);
        return await MappedAsync(NotNull(map), options, offset, limit, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TEntity?> GetFirstAsync(QueryOptions<TEntity> options, CancellationToken cancellationToken = default) =>
        (await ReadAsync(options, null, 1, cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    /// <inheritdoc />
    public async Task<TResult?> GetFirstMappedAsync<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity> options, CancellationToken cancellationToken = default)
    {
        var (offset, limit) = ReadShapes.Window(options, 1);
        return (await MappedAsync(NotNull(map), options, offset, limit, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<TEntity?> GetSingleAsync(QueryOptions<TEntity> options, CancellationToken cancellationToken = default) =>
        (await ReadAsync(options, null, 2, cancellationToken).ConfigureAwait(false)).SingleOrDefault();

    /// <inheritdoc />
    public async Task<PagedResult<TEntity>> GetPagedAsync(PageRequest page, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        NotNull(page);
        ReadShapes.RefuseRowWindow(options, "GetPagedAsync");
        var total = await CountAsync(options, cancellationToken).ConfigureAwait(false);
        var items = await ReadWindowAsync(options, null, page.Skip, page.Take, cancellationToken).ConfigureAwait(false);
        return new PagedResult<TEntity>(items, total, page.PageIndex, page.PageSize);
    }

    /// <inheritdoc />
    public async Task<PagedResult<TResult>> GetPagedAsync<TResult>(PageRequest page, Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        NotNull(page);
        ReadShapes.RefuseRowWindow(options, "GetPagedAsync");
        var total = await CountAsync(options, cancellationToken).ConfigureAwait(false);
        var items = await MappedAsync(NotNull(map), options, page.Skip, page.Take, cancellationToken).ConfigureAwait(false);
        return new PagedResult<TResult>(items, total, page.PageIndex, page.PageSize);
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await ReadyAsync(Whole(options, "CountAsync"), cancellationToken).ConfigureAwait(false)).CountAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<bool> AnyAsync(QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        (await MaterializeAsync(Whole(options, "AnyAsync").Take(1), cancellationToken).ConfigureAwait(false)).Count > 0;

    /// <inheritdoc />
    public async Task<bool> AllAsync(Expression<Func<TEntity, bool>> predicate, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        ReadShapes.RefuseRowWindow(options, "AllAsync");
        return (await MaterializeAsync(Query(options, Negate(NotNull(predicate))).Take(1), cancellationToken).ConfigureAwait(false)).Count == 0;
    }

    /// <inheritdoc />
    public async Task<int> SumAsync(Expression<Func<TEntity, int>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await ReadyAsync(Whole(options, "SumAsync"), cancellationToken).ConfigureAwait(false)).Select(Summed(selector)).SumAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<int?> SumAsync(Expression<Func<TEntity, int?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await ReadyAsync(Whole(options, "SumAsync"), cancellationToken).ConfigureAwait(false)).Select(Summed(selector)).SumAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<long> SumAsync(Expression<Func<TEntity, long>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await ReadyAsync(Whole(options, "SumAsync"), cancellationToken).ConfigureAwait(false)).Select(Summed(selector)).SumAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<long?> SumAsync(Expression<Func<TEntity, long?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await ReadyAsync(Whole(options, "SumAsync"), cancellationToken).ConfigureAwait(false)).Select(Summed(selector)).SumAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<double> SumAsync(Expression<Func<TEntity, double>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await ReadyAsync(Whole(options, "SumAsync"), cancellationToken).ConfigureAwait(false)).Select(Summed(selector)).SumAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<double?> SumAsync(Expression<Func<TEntity, double?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await ReadyAsync(Whole(options, "SumAsync"), cancellationToken).ConfigureAwait(false)).Select(Summed(selector)).SumAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<float> SumAsync(Expression<Func<TEntity, float>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await ReadyAsync(Whole(options, "SumAsync"), cancellationToken).ConfigureAwait(false)).Select(Summed(selector)).SumAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<float?> SumAsync(Expression<Func<TEntity, float?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await ReadyAsync(Whole(options, "SumAsync"), cancellationToken).ConfigureAwait(false)).Select(Summed(selector)).SumAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<decimal> SumAsync(Expression<Func<TEntity, decimal>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await ReadyAsync(Whole(options, "SumAsync"), cancellationToken).ConfigureAwait(false)).Select(Summed(selector)).SumAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<decimal?> SumAsync(Expression<Func<TEntity, decimal?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await (await ReadyAsync(Whole(options, "SumAsync"), cancellationToken).ConfigureAwait(false)).Select(Summed(selector)).SumAsync(cancellationToken).ConfigureAwait(false);

    // ---------------------------------------------------------------- min / max / average

    /// <inheritdoc />
    /// <remarks>
    ///     Pushes down as <c>VALUE MIN(...)</c>; a partition-key-pinning filter scopes the aggregate to one partition. A member
    ///     stored as an enum's labels reads the distinct members present and answers the one with the smallest value.
    /// </remarks>
    public async Task<TResult?> MinAsync<TResult>(Expression<Func<TEntity, TResult>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        LabelEnumOf(NotNull(selector)) is { } labels
            ? EnumOrder.FromValue<TResult>(labels, EnumOrder.Extreme(await MembersPresentAsync(selector, options, "MinAsync", cancellationToken).ConfigureAwait(false), largest: false))
            : await (await ReadyAsync(Whole(options, "MinAsync"), cancellationToken).ConfigureAwait(false)).Select(selector).MinAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    ///     Pushes down as <c>VALUE MAX(...)</c>; a partition-key-pinning filter scopes the aggregate to one partition. A member
    ///     stored as an enum's labels reads the distinct members present and answers the one with the largest value.
    /// </remarks>
    public async Task<TResult?> MaxAsync<TResult>(Expression<Func<TEntity, TResult>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        LabelEnumOf(NotNull(selector)) is { } labels
            ? EnumOrder.FromValue<TResult>(labels, EnumOrder.Extreme(await MembersPresentAsync(selector, options, "MaxAsync", cancellationToken).ConfigureAwait(false), largest: true))
            : await (await ReadyAsync(Whole(options, "MaxAsync"), cancellationToken).ConfigureAwait(false)).Select(selector).MaxAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>The enum a selector reads the labels of (a member stored as its labels, converted or not), or <c>null</c>.</summary>
    private Type? LabelEnumOf(LambdaExpression selector) => EnumOrder.LabelEnumOf(selector, UnitOfWork.EntityModel.IsLabelEnum);

    /// <summary>
    ///     The distinct members a selector of a member stored as labels reads under the options (<c>SELECT DISTINCT VALUE</c>,
    ///     at most the enum's members): the minimum and the maximum pick among them by value, as C# does, where Cosmos DB's
    ///     <c>MIN</c>/<c>MAX</c> would compare the labels' text.
    /// </summary>
    private async Task<IReadOnlyList<object?>> MembersPresentAsync(LambdaExpression selector, QueryOptions<TEntity>? options, string read,
        CancellationToken cancellationToken)
    {
        var body = selector.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } conversion)
        {
            body = conversion.Operand;
        }

        return await ((Task<IReadOnlyList<object?>>)typeof(CosmosReadRepository<TEntity, TKey>)
                .GetMethod(nameof(DistinctOfAsync), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(body.Type)
                .Invoke(this, [Whole(options, read), Expression.Lambda(body, selector.Parameters), cancellationToken])!)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<object?>> DistinctOfAsync<TMember>(IQueryable<TEntity> query, LambdaExpression member,
        CancellationToken cancellationToken) =>
        (await MaterializeAsync(query.Select((Expression<Func<TEntity, TMember>>)member).Distinct(), cancellationToken).ConfigureAwait(false))
        .Cast<object?>().ToList();

    /// <summary>A sum's selector, refused over a member stored as an enum's labels (a label has no arithmetic).</summary>
    private Expression<Func<TEntity, TSum>> Summed<TSum>(Expression<Func<TEntity, TSum>> selector) =>
        LabelEnumOf(NotNull(selector)) is { } labels ? throw EnumOrder.ArithmeticRefusal(labels, "SumAsync") : selector;

    /// <inheritdoc />
    /// <remarks>
    ///     Pushes down as <c>VALUE AVG(...)</c> over the numeric member; an empty match yields <c>0</c>. A member stored as an
    ///     enum's labels is refused: a label has no arithmetic.
    /// </remarks>
    public async Task<double> AverageAsync<TValue>(Expression<Func<TEntity, TValue>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        if (LabelEnumOf(NotNull(selector)) is { } labels)
        {
            throw EnumOrder.ArithmeticRefusal(labels, "AverageAsync");
        }

        // The SDK's AverageAsync overloads are per numeric type; dispatch on the selected member's type.
        var selected = (await ReadyAsync(Whole(options, "AverageAsync"), cancellationToken).ConfigureAwait(false)).Select(selector);
        return selected switch
        {
            IQueryable<int> value => await value.AverageAsync(cancellationToken).ConfigureAwait(false),
            IQueryable<int?> value => (double?)await value.AverageAsync(cancellationToken).ConfigureAwait(false) ?? 0d,
            IQueryable<long> value => await value.AverageAsync(cancellationToken).ConfigureAwait(false),
            IQueryable<long?> value => (double?)await value.AverageAsync(cancellationToken).ConfigureAwait(false) ?? 0d,
            IQueryable<double> value => await value.AverageAsync(cancellationToken).ConfigureAwait(false),
            IQueryable<double?> value => (double?)await value.AverageAsync(cancellationToken).ConfigureAwait(false) ?? 0d,
            IQueryable<float> value => (float)await value.AverageAsync(cancellationToken).ConfigureAwait(false),
            IQueryable<float?> value => (double?)(float?)await value.AverageAsync(cancellationToken).ConfigureAwait(false) ?? 0d,
            IQueryable<decimal> value => (double)(decimal)await value.AverageAsync(cancellationToken).ConfigureAwait(false),
            IQueryable<decimal?> value => (double?)(decimal?)await value.AverageAsync(cancellationToken).ConfigureAwait(false) ?? 0d,
            _ => throw new NotSupportedException(
                $"Average over '{typeof(TValue).Name}' is not supported; select a numeric member."),
        };
    }

    // ---------------------------------------------------------------- grouped reads

    /// <inheritdoc />
    /// <remarks>
    ///     <b>Not yet supported on Cosmos DB.</b> The interpreted projection is validated for the uniform
    ///     contract, then rejected: the SDK's LINQ <c>GroupBy</c> renders an object projection as
    ///     <c>SELECT VALUE {…}</c>, which Cosmos SQL cannot combine with <c>GROUP BY</c>. A correct
    ///     implementation needs a hand-built Cosmos SQL <c>GROUP BY</c> (aliased <c>SELECT</c>, no
    ///     <c>VALUE</c>) — tracked for a follow-up. Until then, group client-side over a filtered read
    ///     (<c>(await GetAllAsync(options)).GroupBy(...)</c>), or use a provider whose <c>GroupBy</c> pushes
    ///     down (relational, MongoDB, Cassandra).
    /// </remarks>
    public Task<IReadOnlyList<TResult>> GroupByAsync<TGroup, TResult>(
        Expression<Func<TEntity, TGroup>> keySelector,
        Expression<Func<IGrouping<TGroup, TEntity>, TResult>> resultSelector,
        Expression<Func<IGrouping<TGroup, TEntity>, bool>>? having = null,
        QueryOptions<TEntity>? options = null,
        CancellationToken cancellationToken = default)
    {
        // Interpret first so the shape rejections stay uniform across providers, then reject the execution.
        GroupInterpreter.Interpret(NotNull(keySelector), NotNull(resultSelector));

        throw new NotSupportedException(
            "GroupBy does not push down on Cosmos DB yet: the SDK's LINQ GroupBy emits 'SELECT VALUE {…}', which " +
            "Cosmos SQL cannot combine with GROUP BY. Group client-side over a filtered read " +
            "((await GetAllAsync(options)).GroupBy(...)), or use a provider whose GroupBy pushes down " +
            "(relational, MongoDB, Cassandra).");
    }

    // ---------------------------------------------------------------- continuation paging

    /// <inheritdoc />
    public async Task<ContinuedResult<TEntity>> GetPageAsync(int pageSize, string? continuationToken = null,
        QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "The page size must be at least 1.");
        }

        ReadShapes.RefuseRowWindow(options, "GetPageAsync");
        RefuseSplitNulls(options, "GetPageAsync");

        var global = GlobalFilter(options);
        var requestOptions = new QueryRequestOptions { MaxItemCount = pageSize };
        var partitionKey = CosmosPartitionKeyInference.Infer(_partitionKeyPath, global, options?.Filter, options?.Specification?.SatisfiedBy());
        if (partitionKey is not null)
        {
            requestOptions.PartitionKey = partitionKey;
        }

        // The SDK walks its native continuation: one ReadNextAsync per call, resumed by the (opaque) token.
        var query = Container
            .GetItemLinqQueryable<TEntity>(continuationToken: continuationToken, requestOptions: requestOptions)
            .ApplyQueryOptions(options);
        if (global is not null)
        {
            query = query.Where(global);
        }

        query = await ReadyAsync(query, cancellationToken).ConfigureAwait(false);
        using var iterator = query.ToFeedIterator();
        if (!iterator.HasMoreResults)
        {
            return new ContinuedResult<TEntity>([], null);
        }

        var response = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
        var items = response.ToList();
        if (options is { IncludePaths.Count: > 0 })
        {
            await CosmosInclude.ApplyAsync(UnitOfWork, items, options.IncludePaths, options.IncludeShapes, cancellationToken).ConfigureAwait(false);
        }

        return new ContinuedResult<TEntity>(items, response.ContinuationToken);
    }

    // ---------------------------------------------------------------- streaming

    /// <inheritdoc />
    public async IAsyncEnumerable<TEntity> GetStreamAsync(QueryOptions<TEntity>? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (options is { IncludePaths.Count: > 0 })
        {
            throw new NotSupportedException("Include does not compose with streaming yet; use GetAllAsync or page explicitly.");
        }

        ReadShapes.RefuseRowWindow(options, "GetStreamAsync");
        RefuseSplitNulls(options, "GetStreamAsync");

        // Streams through the FeedIterator: one response page in memory at a time.
        using var iterator = (await ReadyAsync(Query(options), cancellationToken).ConfigureAwait(false)).ToFeedIterator();
        while (iterator.HasMoreResults)
        {
            foreach (var entity in await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return entity;
            }
        }
    }

    // ---------------------------------------------------------------- explain

    /// <inheritdoc />
    public QueryPlan Explain(QueryOptions<TEntity>? options = null)
    {
        var notes = new List<string>();
        if (options is { IncludePaths.Count: > 0 })
        {
            notes.Add($"Each include ({string.Join(", ", options.IncludePaths)}) runs as one second query over the related container " +
                      "(IN over the parents' keys); a shape's filter joins its WHERE, and its order and limit apply per parent as the rows are " +
                      "stitched, so every row the filter keeps travels.");
        }

        if (options is not null && ReadShapes.Sorts(options) is { Count: > 0 } sorts && SplitsRead(sorts[0]))
        {
            notes.Add(UnitOfWork.EntityModel.IsLabelEnum(Unconverted(sorts[0].Key).Body.Type)
                ? "An enum stored as its labels orders by the values of its members, as C# does, where an ORDER BY would order the " +
                  "labels' text: the read runs one query per member, in the order of their values (and one for the rows whose key is " +
                  "null, where they go), concatenated."
                : "Nulls placed against Cosmos DB's order (nulls and undefined sort as the smallest value) read as two queries, " +
                  "the rows whose key is null and the rest, concatenated in the order asked.");
        }

        if (options is { SkipCount: not null } or { TakeCount: not null })
        {
            notes.Add("Skip/Take run as OFFSET/LIMIT; no count runs.");
        }

        // Build the shaped queryable directly (not via Query) so an explain never throws on shaping issues.
        var global = GlobalFilter(options);
        if (global is not null)
        {
            notes.Add("A global query filter is ANDed into this query; IgnoringQueryFilters() opts out.");
        }

        var shaped = Container
            .GetItemLinqQueryable<TEntity>(allowSynchronousQueryExecution: true)
            .ApplyQueryOptions(options, nullsPlaced: true);
        var filtered = global is null ? shaped : shaped.Where(global);
        notes.AddRange(CosmosPredicates.Notes(filtered.Expression, UnitOfWork));
        var definition = CosmosPredicates.Unresolved(filtered, UnitOfWork).ToQueryDefinition();

        var partitionKey = CosmosPartitionKeyInference.Infer(_partitionKeyPath, global, options?.Filter, options?.Specification?.SatisfiedBy());
        notes.Add(partitionKey is not null
            ? "Scoped to a single partition (partition key inferred from the filter)."
            : "Cross-partition query: no partition key is pinned by the filter.");

        return new QueryPlan("CosmosDb", definition.QueryText,
            definition.GetQueryParameters().Select(parameter => (object?)parameter.Value).ToList(), residual: null,
            serverSideFiltering: false, clientEvaluation: false, partitionScoped: partitionKey is not null, notes);
    }

    // ---------------------------------------------------------------- distinct values

    /// <inheritdoc />
    /// <remarks>Runs as <c>SELECT DISTINCT VALUE …</c> under the read's <c>WHERE</c>: only the distinct values travel.</remarks>
    public async Task<IReadOnlyList<TResult>> GetDistinctAsync<TResult>(Expression<Func<TEntity, TResult>> selector,
        Expression<Func<TEntity, bool>>? filter = null, QueryOptions<TEntity>? options = null,
        CancellationToken cancellationToken = default)
    {
        NotNull(selector);
        ReadShapes.RefuseForDistinct(options);
        return await MaterializeAsync(Query(options, filter).Select(selector).Distinct(), cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- read shapes

    /// <summary>An entity read honouring the options' <c>Skip</c>/<c>Take</c> within the read's own limit, includes loaded.</summary>
    private Task<List<TEntity>> ReadAsync(QueryOptions<TEntity>? options, Expression<Func<TEntity, bool>>? extraFilter, int? limit,
        CancellationToken cancellationToken)
    {
        var (offset, effective) = ReadShapes.Window(options, limit);
        return ReadWindowAsync(options, extraFilter, offset, effective, cancellationToken);
    }

    private async Task<List<TEntity>> ReadWindowAsync(QueryOptions<TEntity>? options, Expression<Func<TEntity, bool>>? extraFilter,
        int? offset, int? limit, CancellationToken cancellationToken)
    {
        var entities = await SegmentsAsync(options, extraFilter, offset, limit, query => query, cancellationToken).ConfigureAwait(false);
        if (options is { IncludePaths.Count: > 0 })
        {
            await CosmosInclude.ApplyAsync(UnitOfWork, entities, options.IncludePaths, options.IncludeShapes, cancellationToken)
                .ConfigureAwait(false);
        }

        return entities;
    }

    /// <summary>
    ///     A mapped read: the SDK's projection, or — when the map counts relations — the parents read whole, their counts
    ///     grouped on the server, and the map run in memory over both.
    /// </summary>
    private async Task<List<TResult>> MappedAsync<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options,
        int? offset, int? limit, CancellationToken cancellationToken)
    {
        if (options is { IncludePaths.Count: > 0 })
        {
            throw new NotSupportedException(
                "Include applies to entity reads; project after including, or drop the includes from the mapped read.");
        }

        if (RelationCountInterpreter.Interpret(map, property => CosmosInclude.IsRelation(UnitOfWork, typeof(TEntity), property)) is { } counted)
        {
            var entities = await SegmentsAsync(options, null, offset, limit, query => query, cancellationToken).ConfigureAwait(false);
            var counts = await CosmosInclude.CountAsync(UnitOfWork, entities, counted.Counts, cancellationToken).ConfigureAwait(false);
            var project = counted.Projection.Compile();
            return entities.Select((entity, index) => project(entity, counts[index])).ToList();
        }

        return await SegmentsAsync(options, null, offset, limit, query => query.Select(map), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Runs the read with its window. Cosmos DB orders nulls and undefined as the smallest value and orders by property
    ///     paths alone, so the first sort reads as several queries, concatenated in the order asked, when an <c>ORDER BY</c>
    ///     cannot say it: a placement of nulls against that order reads the rows whose key is null and the rest; an enum
    ///     stored as its labels, which an <c>ORDER BY</c> would order by the labels' text, reads the rows of each member in
    ///     the order of the members' values (and the rows whose key is null where they go), as C# orders the enum. The window
    ///     is carried across the parts (a count of a part runs only when the skip passes it whole).
    /// </summary>
    private async Task<List<TResult>> SegmentsAsync<TResult>(QueryOptions<TEntity>? options, Expression<Func<TEntity, bool>>? extraFilter,
        int? offset, int? limit, Func<IQueryable<TEntity>, IQueryable<TResult>> project, CancellationToken cancellationToken)
    {
        var segments = Segments(ReadShapes.Sorts(options));
        if (segments is null)
        {
            return await MaterializeAsync(project(Window(Query(options, extraFilter), options, offset, limit)), cancellationToken)
                .ConfigureAwait(false);
        }

        var results = new List<TResult>();
        var skip = offset ?? 0;
        var remaining = limit;
        for (var index = 0; index < segments.Count && remaining is not 0; index++)
        {
            var segment = And(extraFilter, segments[index]);
            var rows = await MaterializeAsync(project(Window(Query(options, segment), options, skip > 0 ? skip : null, remaining)),
                cancellationToken).ConfigureAwait(false);
            results.AddRange(rows);
            remaining -= rows.Count;

            if (skip > 0 && index + 1 < segments.Count)
            {
                // The window began inside this part when it returned rows; otherwise the skip passes it whole.
                skip = rows.Count > 0
                    ? 0
                    : Math.Max(0, skip - await (await ReadyAsync(Query(options, segment), cancellationToken).ConfigureAwait(false))
                        .CountAsync(cancellationToken).ConfigureAwait(false));
            }
        }

        return results;
    }

    /// <summary>
    ///     The parts a read with <paramref name="sorts" /> splits into, in the order they are read, or <c>null</c> when one
    ///     <c>ORDER BY</c> says the order. Only the first sort can split the read; a later one that would need it is refused.
    /// </summary>
    private IReadOnlyList<Expression<Func<TEntity, bool>>>? Segments(IReadOnlyList<MemorySort> sorts)
    {
        if (sorts.Skip(1).Any(sort => SplitsRead(sort)))
        {
            throw SplitRefusal("a later sort");
        }

        if (sorts.Count == 0 || !SplitsRead(sorts[0]))
        {
            return null;
        }

        var first = sorts[0];
        var isNull = IsNull(first.Key);
        var nullsFirst = first.Nulls switch
        {
            NullsOrder.First => true,
            NullsOrder.Last => false,
            _ => !first.Descending,
        };

        List<Expression<Func<TEntity, bool>>> values;
        var keyType = Unconverted(first.Key).Body.Type;
        if (UnitOfWork.EntityModel.IsLabelEnum(keyType))
        {
            var members = EnumOrder.ByValue(keyType).ToList();
            if (first.Descending)
            {
                members.Reverse();
            }

            values = members.Select(member => IsMember(first.Key, member)).ToList();
            if (Nullable.GetUnderlyingType(keyType) is null)
            {
                // A member that is not nullable is never null: the members' rows are all there is.
                return values;
            }
        }
        else
        {
            values = [Expression.Lambda<Func<TEntity, bool>>(Expression.Not(isNull.Body), isNull.Parameters)];
        }

        return nullsFirst ? [isNull, .. values] : [.. values, isNull];
    }

    /// <summary>
    ///     Whether a sort splits the read: it places nulls against Cosmos DB's order, or its key is an enum stored as its
    ///     labels.
    /// </summary>
    private bool SplitsRead(MemorySort sort) =>
        !ReadShapes.IsNullsSmallest(sort.Nulls, sort.Descending) || UnitOfWork.EntityModel.IsLabelEnum(Unconverted(sort.Key).Body.Type);

    /// <summary><c>x =&gt; new[] { member }.Contains(x.Key)</c>: the rows holding one member's label.</summary>
    private static Expression<Func<TEntity, bool>> IsMember(LambdaExpression key, Enum member)
    {
        var parameter = Expression.Parameter(typeof(TEntity), "x");
        var body = new ParameterSwap(key.Parameters[0], parameter).Visit(Unconverted(key).Body);
        var array = Array.CreateInstance(body.Type, 1);
        array.SetValue(member, 0);
        var contains = Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [body.Type], Expression.Constant(array), body);
        return Expression.Lambda<Func<TEntity, bool>>(contains, parameter);
    }

    private static LambdaExpression Unconverted(LambdaExpression key)
    {
        var body = key.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } conversion)
        {
            body = conversion.Operand;
        }

        return ReferenceEquals(body, key.Body) ? key : Expression.Lambda(body, key.Parameters);
    }

    private IQueryable<TEntity> Window(IQueryable<TEntity> query, QueryOptions<TEntity>? options, int? offset, int? limit)
    {
        if (offset is not null)
        {
            query = Ordered(query, options).Skip(offset.Value);
        }

        return limit is null ? query : query.Take(limit.Value);
    }

    /// <summary>Refuses a sort that splits the read on a read that cannot split (paging by continuation, streaming).</summary>
    private void RefuseSplitNulls(QueryOptions<TEntity>? options, string read)
    {
        if (ReadShapes.Sorts(options).Any(SplitsRead))
        {
            throw SplitRefusal(read);
        }
    }

    private static NotSupportedException SplitRefusal(string where) =>
        new($"Cosmos DB sorts nulls and undefined as the smallest value, orders by property paths alone, and would order an enum " +
            $"stored as its labels by the labels' text, so a placement of nulls against that order, or an order by such an enum, " +
            $"cannot apply to {where}: it reads as one query per part (the rows whose key is null, the rows of each member in " +
            "the order of their values), which works for the first sort of an entity or mapped read. Make it the first sort, or " +
            "order the rows client-side.");

    /// <summary><c>x =&gt; !IS_DEFINED(x.Key) || IS_NULL(x.Key)</c>.</summary>
    private static Expression<Func<TEntity, bool>> IsNull(LambdaExpression key)
    {
        var parameter = Expression.Parameter(typeof(TEntity), "x");
        var member = new ParameterSwap(key.Parameters[0], parameter).Visit(key.Body);
        var boxed = Expression.Convert(member, typeof(object));
        var defined = Expression.Call(typeof(CosmosLinqExtensions), nameof(CosmosLinqExtensions.IsDefined), null, boxed);
        var isNull = Expression.Call(typeof(CosmosLinqExtensions), nameof(CosmosLinqExtensions.IsNull), null, boxed);
        return Expression.Lambda<Func<TEntity, bool>>(Expression.OrElse(Expression.Not(defined), isNull), parameter);
    }

    private static Expression<Func<TEntity, bool>> And(Expression<Func<TEntity, bool>>? left, Expression<Func<TEntity, bool>> right)
    {
        if (left is null)
        {
            return right;
        }

        var body = new ParameterSwap(right.Parameters[0], left.Parameters[0]).Visit(right.Body);
        return Expression.Lambda<Func<TEntity, bool>>(Expression.AndAlso(left.Body, body), left.Parameters);
    }

    /// <summary>The shaped query for a read over every matching row, which refuses the options' <c>Skip</c>/<c>Take</c>.</summary>
    private IQueryable<TEntity> Whole(QueryOptions<TEntity>? options, string read)
    {
        ReadShapes.RefuseRowWindow(options, read);
        return Query(options);
    }

    private sealed class ParameterSwap(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
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

    /// <summary>Builds the shaped query for the supplied options, optionally with an extra predicate.</summary>
    /// <param name="options">The query options, or <c>null</c>.</param>
    /// <param name="extraFilter">An optional extra predicate (e.g. an id or ad-hoc filter); it also feeds partition-key inference.</param>
    /// <returns>The shaped query.</returns>
    protected IQueryable<TEntity> Query(QueryOptions<TEntity>? options, Expression<Func<TEntity, bool>>? extraFilter = null)
    {
        var global = GlobalFilter(options);
        var query = Container
            .GetItemLinqQueryable<TEntity>(allowSynchronousQueryExecution: true, requestOptions: RequestOptions(options, extraFilter, global))
            .ApplyQueryOptions(options, nullsPlaced: true);
        if (global is not null)
        {
            query = query.Where(global);
        }

        return extraFilter is null ? query : query.Where(extraFilter);
    }

    /// <summary>
    ///     Scopes the query to a single partition when any of its filters — the options' predicate/specification,
    ///     the extra predicate or the global filter — pins the partition key, otherwise lets it run cross-partition.
    ///     Automatic — a tenant filter such as <c>x =&gt; x.TenantId == tenant</c> on the partition key already scopes it.
    /// </summary>
    private QueryRequestOptions? RequestOptions(QueryOptions<TEntity>? options, Expression<Func<TEntity, bool>>? extraFilter,
        Expression<Func<TEntity, bool>>? globalFilter)
    {
        var partitionKey = CosmosPartitionKeyInference.Infer(
            _partitionKeyPath, extraFilter, globalFilter, options?.Filter, options?.Specification?.SatisfiedBy());
        return partitionKey is null ? null : new QueryRequestOptions { PartitionKey = partitionKey };
    }

    /// <summary>The global filter for this entity, unless the options opt out of query filters.</summary>
    private Expression<Func<TEntity, bool>>? GlobalFilter(QueryOptions<TEntity>? options) =>
        options is { IgnoreQueryFilters: true } ? null : UnitOfWork.GlobalFilter<TEntity>();

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

    private async Task<List<T>> MaterializeAsync<T>(IQueryable<T> query, CancellationToken cancellationToken)
    {
        var results = new List<T>();
        using var iterator = (await ReadyAsync(query, cancellationToken).ConfigureAwait(false)).ToFeedIterator();
        while (iterator.HasMoreResults)
        {
            results.AddRange(await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>
    ///     The query as the SDK translates it: an array's <c>Contains</c> and <c>string.Equals(a, b, comparison)</c> in the
    ///     shapes the SDK reads, and each test across a relation resolved into the related keys — read first, one
    ///     <c>SELECT DISTINCT VALUE</c> over the related container per test (a relation crosses containers).
    /// </summary>
    private Task<IQueryable<T>> ReadyAsync<T>(IQueryable<T> query, CancellationToken cancellationToken) =>
        CosmosPredicates.ReadyAsync(query, UnitOfWork, cancellationToken);

    /// <summary>The query as the SDK translates it, for the synchronous surface.</summary>
    private IQueryable<T> Ready<T>(IQueryable<T> query) => ReadyAsync(query, CancellationToken.None).GetAwaiter().GetResult();

    private static Expression<Func<TEntity, bool>> Negate(Expression<Func<TEntity, bool>> predicate) =>
        Expression.Lambda<Func<TEntity, bool>>(Expression.Not(predicate.Body), predicate.Parameters);

    private static T NotNull<T>(T value) where T : class => value ?? throw new ArgumentNullException(typeof(T).Name);
}
