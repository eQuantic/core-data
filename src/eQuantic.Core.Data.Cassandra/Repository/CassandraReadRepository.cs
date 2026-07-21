using System.Linq.Expressions;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Read;
using eQuantic.Linq.Expressions;
using eQuantic.Linq.Specification;
using eQuantic.Linq.Web;
using global::Cassandra;

namespace eQuantic.Core.Data.Cassandra.Repository;

/// <summary>
///     The native Apache Cassandra read repository. A <see cref="QueryOptions{TEntity}" /> filter is translated to
///     a CQL <c>WHERE</c> over the partition/clustering keys; non-key predicates require <c>.AllowFiltering()</c>.
///     Sorting is limited to clustering keys, and paging fetches the first <c>skip+take</c> rows then slices
///     client-side (Cassandra has no OFFSET). Synchronous members delegate to the asynchronous ones.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TKey">The key type.</typeparam>
public abstract class CassandraReadRepository<TEntity, TKey> :
    IQueryableReadRepository<TEntity, TKey>,
    IAsyncQueryableReadRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
{
    /// <summary>The unit of work backing this repository.</summary>
    protected readonly CassandraUnitOfWork UnitOfWork;

    /// <summary>The session.</summary>
    protected readonly ISession Session;

    private readonly CassandraEntityConfiguration _configuration;

    /// <summary>Initializes the repository over a unit of work.</summary>
    /// <param name="unitOfWork">The queryable unit of work (a <see cref="CassandraUnitOfWork" />).</param>
    protected CassandraReadRepository(IQueryableUnitOfWork unitOfWork)
    {
        UnitOfWork = unitOfWork as CassandraUnitOfWork
                     ?? throw new ArgumentException($"The unit of work must be a {nameof(CassandraUnitOfWork)}.", nameof(unitOfWork));
        Session = UnitOfWork.GetSession();
        _configuration = UnitOfWork.Configuration<TEntity>();
    }

    // ---------------------------------------------------------------- asynchronous reads

    /// <inheritdoc />
    public async Task<TEntity?> GetAsync(TKey id, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        var statement = new SimpleStatement($"SELECT * FROM {_configuration.TableName} WHERE {_configuration.KeyColumn} = ? LIMIT 1", id);
        var row = (await Session.ExecuteAsync(statement).ConfigureAwait(false)).FirstOrDefault();
        return row is null ? null : CassandraMapper.Materialize<TEntity>(_configuration, row);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<TEntity>> GetAllAsync(QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await SelectAsync(options, null, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public Task<IEnumerable<TEntity>> GetFilteredAsync(Expression<Func<TEntity, bool>> filter, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        GetAllAsync(With(options, filter), cancellationToken);

    /// <inheritdoc />
    public Task<IEnumerable<TEntity>> AllMatchingAsync(ISpecification<TEntity> specification, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        GetAllAsync(With(options, specification.SatisfiedBy()), cancellationToken);

    /// <inheritdoc />
    public async Task<IEnumerable<TResult>> GetMappedAsync<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        (await SelectAsync(options, null, cancellationToken).ConfigureAwait(false)).Select(map.Compile()).ToList();

    /// <inheritdoc />
    public async Task<TEntity?> GetFirstAsync(QueryOptions<TEntity> options, CancellationToken cancellationToken = default) =>
        (await SelectAsync(options, 1, cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    /// <inheritdoc />
    public async Task<TResult?> GetFirstMappedAsync<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity> options, CancellationToken cancellationToken = default) =>
        (await SelectAsync(options, 1, cancellationToken).ConfigureAwait(false)).Select(map.Compile()).FirstOrDefault();

    /// <inheritdoc />
    public async Task<TEntity?> GetSingleAsync(QueryOptions<TEntity> options, CancellationToken cancellationToken = default) =>
        (await SelectAsync(options, 2, cancellationToken).ConfigureAwait(false)).SingleOrDefault();

    /// <inheritdoc />
    public async Task<PagedResult<TEntity>> GetPagedAsync(PageRequest page, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        var total = await CountAsync(options, cancellationToken).ConfigureAwait(false);
        var rows = await SelectAsync(options, page.Skip + page.Take, cancellationToken).ConfigureAwait(false);
        return new PagedResult<TEntity>(rows.Skip(page.Skip).Take(page.Take).ToList(), total, page.PageIndex, page.PageSize);
    }

    /// <inheritdoc />
    public async Task<PagedResult<TResult>> GetPagedAsync<TResult>(PageRequest page, Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        var total = await CountAsync(options, cancellationToken).ConfigureAwait(false);
        var rows = await SelectAsync(options, page.Skip + page.Take, cancellationToken).ConfigureAwait(false);
        var items = rows.Skip(page.Skip).Take(page.Take).Select(map.Compile()).ToList();
        return new PagedResult<TResult>(items, total, page.PageIndex, page.PageSize);
    }

    /// <inheritdoc />
    public Task<long> CountAsync(QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        var (where, values, allowFiltering) = WhereFor(options);
        var cql = $"SELECT COUNT(*) FROM {_configuration.TableName}"
                  + (where.Length > 0 ? $" WHERE {where}" : string.Empty)
                  + (allowFiltering ? " ALLOW FILTERING" : string.Empty);
        return CountAsync(cql, values, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> AnyAsync(QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        (await SelectAsync(options, 1, cancellationToken).ConfigureAwait(false)).Count > 0;

    /// <inheritdoc />
    public async Task<bool> AllAsync(Expression<Func<TEntity, bool>> predicate, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        (await SelectAsync(options, null, cancellationToken).ConfigureAwait(false)).All(predicate.Compile());

    /// <inheritdoc />
    public async Task<int> SumAsync(Expression<Func<TEntity, int>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) => (await SelectAsync(options, null, cancellationToken).ConfigureAwait(false)).Sum(selector.Compile());

    /// <inheritdoc />
    public async Task<int?> SumAsync(Expression<Func<TEntity, int?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) => (await SelectAsync(options, null, cancellationToken).ConfigureAwait(false)).Sum(selector.Compile());

    /// <inheritdoc />
    public async Task<long> SumAsync(Expression<Func<TEntity, long>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) => (await SelectAsync(options, null, cancellationToken).ConfigureAwait(false)).Sum(selector.Compile());

    /// <inheritdoc />
    public async Task<long?> SumAsync(Expression<Func<TEntity, long?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) => (await SelectAsync(options, null, cancellationToken).ConfigureAwait(false)).Sum(selector.Compile());

    /// <inheritdoc />
    public async Task<double> SumAsync(Expression<Func<TEntity, double>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) => (await SelectAsync(options, null, cancellationToken).ConfigureAwait(false)).Sum(selector.Compile());

    /// <inheritdoc />
    public async Task<double?> SumAsync(Expression<Func<TEntity, double?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) => (await SelectAsync(options, null, cancellationToken).ConfigureAwait(false)).Sum(selector.Compile());

    /// <inheritdoc />
    public async Task<float> SumAsync(Expression<Func<TEntity, float>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) => (await SelectAsync(options, null, cancellationToken).ConfigureAwait(false)).Sum(selector.Compile());

    /// <inheritdoc />
    public async Task<float?> SumAsync(Expression<Func<TEntity, float?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) => (await SelectAsync(options, null, cancellationToken).ConfigureAwait(false)).Sum(selector.Compile());

    /// <inheritdoc />
    public async Task<decimal> SumAsync(Expression<Func<TEntity, decimal>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) => (await SelectAsync(options, null, cancellationToken).ConfigureAwait(false)).Sum(selector.Compile());

    /// <inheritdoc />
    public async Task<decimal?> SumAsync(Expression<Func<TEntity, decimal?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) => (await SelectAsync(options, null, cancellationToken).ConfigureAwait(false)).Sum(selector.Compile());

    // ---------------------------------------------------------------- synchronous reads (delegate)

    /// <inheritdoc />
    public TEntity? Get(TKey id, QueryOptions<TEntity>? options = null) => GetAsync(id, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public IEnumerable<TEntity> GetAll(QueryOptions<TEntity>? options = null) => GetAllAsync(options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public IEnumerable<TEntity> GetFiltered(Expression<Func<TEntity, bool>> filter, QueryOptions<TEntity>? options = null) => GetFilteredAsync(filter, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public IEnumerable<TEntity> AllMatching(ISpecification<TEntity> specification, QueryOptions<TEntity>? options = null) => AllMatchingAsync(specification, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public IEnumerable<TResult> GetMapped<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null) => GetMappedAsync(map, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public TEntity? GetFirst(QueryOptions<TEntity> options) => GetFirstAsync(options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public TResult? GetFirstMapped<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity> options) => GetFirstMappedAsync(map, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public TEntity? GetSingle(QueryOptions<TEntity> options) => GetSingleAsync(options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public PagedResult<TEntity> GetPaged(PageRequest page, QueryOptions<TEntity>? options = null) => GetPagedAsync(page, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public PagedResult<TResult> GetPaged<TResult>(PageRequest page, Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null) => GetPagedAsync(page, map, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public long Count(QueryOptions<TEntity>? options = null) => CountAsync(options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public bool Any(QueryOptions<TEntity>? options = null) => AnyAsync(options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public bool All(Expression<Func<TEntity, bool>> predicate, QueryOptions<TEntity>? options = null) => AllAsync(predicate, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public int Sum(Expression<Func<TEntity, int>> selector, QueryOptions<TEntity>? options = null) => SumAsync(selector, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public int? Sum(Expression<Func<TEntity, int?>> selector, QueryOptions<TEntity>? options = null) => SumAsync(selector, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public long Sum(Expression<Func<TEntity, long>> selector, QueryOptions<TEntity>? options = null) => SumAsync(selector, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public long? Sum(Expression<Func<TEntity, long?>> selector, QueryOptions<TEntity>? options = null) => SumAsync(selector, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public double Sum(Expression<Func<TEntity, double>> selector, QueryOptions<TEntity>? options = null) => SumAsync(selector, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public double? Sum(Expression<Func<TEntity, double?>> selector, QueryOptions<TEntity>? options = null) => SumAsync(selector, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public float Sum(Expression<Func<TEntity, float>> selector, QueryOptions<TEntity>? options = null) => SumAsync(selector, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public float? Sum(Expression<Func<TEntity, float?>> selector, QueryOptions<TEntity>? options = null) => SumAsync(selector, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public decimal Sum(Expression<Func<TEntity, decimal>> selector, QueryOptions<TEntity>? options = null) => SumAsync(selector, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public decimal? Sum(Expression<Func<TEntity, decimal?>> selector, QueryOptions<TEntity>? options = null) => SumAsync(selector, options).GetAwaiter().GetResult();

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

    private async Task<List<TEntity>> SelectAsync(QueryOptions<TEntity>? options, int? limit, CancellationToken cancellationToken)
    {
        var (where, values, allowFiltering) = WhereFor(options);

        var cql = $"SELECT * FROM {_configuration.TableName}";
        if (where.Length > 0)
        {
            cql += $" WHERE {where}";
        }

        cql += OrderBy(options);
        if (limit is { } take)
        {
            cql += $" LIMIT {take}";
        }

        if (allowFiltering)
        {
            cql += " ALLOW FILTERING";
        }

        var rows = await Session.ExecuteAsync(new SimpleStatement(cql, values)).ConfigureAwait(false);
        return rows.Select(row => CassandraMapper.Materialize<TEntity>(_configuration, row)).ToList();
    }

    private async Task<long> CountAsync(string cql, object?[] values, CancellationToken cancellationToken)
    {
        var rows = await Session.ExecuteAsync(new SimpleStatement(cql, values)).ConfigureAwait(false);
        return rows.First().GetValue<long>(0);
    }

    private (string Where, object?[] Values, bool AllowFiltering) WhereFor(QueryOptions<TEntity>? options)
    {
        var (where, values, requiresFiltering) = CassandraCql.Where(_configuration, options);
        if (requiresFiltering && !CassandraCql.AllowFilteringOptedIn(options))
        {
            throw new NotSupportedException(
                "This filter targets non-key columns; call .AllowFiltering() to opt into a scan, or filter by the partition/clustering keys.");
        }

        return (where, values, requiresFiltering);
    }

    private string OrderBy(QueryOptions<TEntity>? options)
    {
        if (options?.Sortings is not { Count: > 0 } sortings)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var sort in sortings)
        {
            var column = sort.KeySelector.GetMemberName();
            if (!_configuration.IsClusteringKey(column))
            {
                throw new NotSupportedException($"Cassandra can only ORDER BY clustering keys; '{column}' is not one.");
            }

            parts.Add($"{column} {(sort.Direction == SortDirection.Descending ? "DESC" : "ASC")}");
        }

        return " ORDER BY " + string.Join(", ", parts);
    }

    private static QueryOptions<TEntity> With(QueryOptions<TEntity>? options, Expression<Func<TEntity, bool>> filter) =>
        (options ?? new QueryOptions<TEntity>()).Where(filter);
}
