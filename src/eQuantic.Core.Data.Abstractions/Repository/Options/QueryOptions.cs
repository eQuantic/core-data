using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using eQuantic.Linq.Expressions;
using eQuantic.Linq.Specification;
using eQuantic.Linq.Web;

namespace eQuantic.Core.Data.Repository.Options;

/// <summary>
/// Describes how a read query should be shaped: filtering, related data to
/// include, sorting, change tracking and query-filter behaviour.
/// </summary>
/// <remarks>
/// A single <see cref="QueryOptions{TEntity}"/> instance replaces the large set
/// of overloads previously exposed by the read repository interfaces. Options
/// are composed through a fluent API and every method returns the same instance
/// so calls can be chained.
/// <para>
/// Filters are best authored typed and fluent — <c>Where</c>/<c>And</c>/<c>Or</c>
/// take a member selector (or a path string for dynamic columns), an operator and a
/// value, folding left to right (<c>Where(a).And(b).Or(c)</c> is <c>(a AND b) OR c</c>).
/// The raw <see cref="Where(string, QueryStringOptions)"/> overload is meant for the boundary
/// where a filter arrives as a query string; prefer the typed form for filters written in code.
/// </para>
/// </remarks>
/// <typeparam name="TEntity">The type of the entity being queried.</typeparam>
public class QueryOptions<TEntity>
    where TEntity : class
{
    private readonly HashSet<string> _includePaths = new();
    private readonly Dictionary<string, IncludeShape> _includeShapes = new(StringComparer.Ordinal);
    private readonly List<QuerySort<TEntity>> _sortings = new();
    private readonly Dictionary<QuerySort<TEntity>, NullsOrder> _nulls = new(ReferenceEqualityComparer.Instance);
    private QueryFilterBuilder<TEntity>? _filterBuilder;

    /// <summary>
    /// Gets the transformation applied to the query before filtering, or
    /// <c>null</c> when none was supplied.
    /// </summary>
    public Func<IQueryable<TEntity>, IQueryable<TEntity>>? BeforeCustomization { get; private set; }

    /// <summary>
    /// Gets the transformation applied to the query after filtering and sorting,
    /// or <c>null</c> when none was supplied.
    /// </summary>
    public Func<IQueryable<TEntity>, IQueryable<TEntity>>? AfterCustomization { get; private set; }

    /// <summary>
    /// Gets the specification used to filter the query, or <c>null</c> when no
    /// specification was supplied.
    /// </summary>
    public ISpecification<TEntity>? Specification { get; private set; }

    /// <summary>
    /// Gets the predicate used to filter the query, or <c>null</c> when no
    /// predicate was supplied.
    /// </summary>
    public Expression<Func<TEntity, bool>>? Filter { get; private set; }

    /// <summary>
    /// Gets the related property paths to eagerly load with the query.
    /// </summary>
    public IReadOnlyCollection<string> IncludePaths => _includePaths;

    /// <summary>
    /// Gets the shapes of the included collections that carry a filter, an order or a limit, keyed by
    /// navigation path. An include without a shape loads every related entity and has no entry here.
    /// </summary>
    public IReadOnlyDictionary<string, IncludeShape> IncludeShapes => _includeShapes;

    /// <summary>
    /// Gets the ordered set of sortings applied to the query.
    /// </summary>
    public IReadOnlyList<QuerySort<TEntity>> Sortings => _sortings;

    /// <summary>
    /// Gets how many rows the read skips, in its order, before it returns any, or <c>null</c> when it skips none.
    /// </summary>
    public int? SkipCount { get; private set; }

    /// <summary>
    /// Gets how many rows the read returns at most, or <c>null</c> when it returns every row that matches.
    /// </summary>
    public int? TakeCount { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the query is executed without change tracking.
    /// </summary>
    public bool AsNoTracking { get; private set; }

    /// <summary>
    /// Gets a value indicating whether global query filters are ignored for this query.
    /// </summary>
    public bool IgnoreQueryFilters { get; private set; }

    /// <summary>
    /// Gets the optional tag associated with the query, useful for logging and diagnostics.
    /// </summary>
    public string? Tag { get; private set; }

    // ---------------------------------------------------------------- filtering (fluent, typed)

    /// <summary>
    /// Adds a comparison clause combined with AND, e.g. <c>o =&gt; o.Total</c> greater than <c>100</c>.
    /// Clauses fold left to right; consecutive AND clauses flatten into a single filter.
    /// </summary>
    /// <typeparam name="TMember">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="op">The comparison operator.</param>
    /// <param name="value">The value to compare against.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    public QueryOptions<TEntity> Where<TMember>(Expression<Func<TEntity, TMember>> selector, FilterOperator op, TMember value) =>
        Fold(builder => builder.And(selector, op, value));

    /// <summary>
    /// Adds a comparison clause combined with AND by path string (for dynamic column names); the
    /// operator and value stay typed. The path can carry aggregate/method segments (<c>items.count()</c>).
    /// </summary>
    /// <param name="path">The member path.</param>
    /// <param name="op">The comparison operator.</param>
    /// <param name="value">The value to compare against.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    public QueryOptions<TEntity> Where(string path, FilterOperator op, object? value) =>
        Fold(builder => builder.And(path, op, value));

    /// <summary>Adds a comparison clause combined with AND (reads naturally after <see cref="Where{TMember}"/>).</summary>
    /// <typeparam name="TMember">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="op">The comparison operator.</param>
    /// <param name="value">The value to compare against.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    public QueryOptions<TEntity> And<TMember>(Expression<Func<TEntity, TMember>> selector, FilterOperator op, TMember value) =>
        Where(selector, op, value);

    /// <summary>Adds a comparison clause combined with AND by path string.</summary>
    /// <param name="path">The member path.</param>
    /// <param name="op">The comparison operator.</param>
    /// <param name="value">The value to compare against.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    public QueryOptions<TEntity> And(string path, FilterOperator op, object? value) =>
        Where(path, op, value);

    /// <summary>Adds a comparison clause combined with OR: everything built so far <c>OR</c> this clause.</summary>
    /// <typeparam name="TMember">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="op">The comparison operator.</param>
    /// <param name="value">The value to compare against.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    public QueryOptions<TEntity> Or<TMember>(Expression<Func<TEntity, TMember>> selector, FilterOperator op, TMember value) =>
        Fold(builder => builder.Or(selector, op, value));

    /// <summary>Adds a comparison clause combined with OR by path string.</summary>
    /// <param name="path">The member path.</param>
    /// <param name="op">The comparison operator.</param>
    /// <param name="value">The value to compare against.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    public QueryOptions<TEntity> Or(string path, FilterOperator op, object? value) =>
        Fold(builder => builder.Or(path, op, value));

    // ---------------------------------------------------------------- filtering (whole filters)

    /// <summary>
    /// Filters the query using the supplied specification.
    /// </summary>
    /// <param name="specification">The specification to apply.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="specification"/> is <c>null</c>.</exception>
    public QueryOptions<TEntity> Where(ISpecification<TEntity> specification)
    {
        Specification = specification ?? throw new ArgumentNullException(nameof(specification));
        return this;
    }

    /// <summary>
    /// Filters the query using the supplied predicate.
    /// </summary>
    /// <param name="filter">The predicate to apply.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="filter"/> is <c>null</c>.</exception>
    public QueryOptions<TEntity> Where(Expression<Func<TEntity, bool>> filter)
    {
        Filter = filter ?? throw new ArgumentNullException(nameof(filter));
        return this;
    }

    /// <summary>
    /// Filters the query using a serialized <see cref="ExpressionModel{TEntity}"/> — a filter built
    /// in code or received over the wire goes straight in, no query string in the middle.
    /// </summary>
    /// <param name="model">The serializable filter model.</param>
    /// <param name="serializer">Optional serializer; the default applies when omitted.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="model"/> is <c>null</c>.</exception>
    public QueryOptions<TEntity> Where(ExpressionModel<TEntity> model, ExpressionSerializer? serializer = null)
    {
        if (model == null)
        {
            throw new ArgumentNullException(nameof(model));
        }

        Filter = model.ToPredicate(serializer);
        return this;
    }

    /// <summary>
    /// Filters the query using an <c>eQuantic.Linq.Web</c> filter expression, e.g.
    /// <c>name:eq(John),age:gt(18)</c>. Intended for the boundary where a filter arrives as a
    /// query string; for filters written in code prefer the typed <c>Where</c>/<c>And</c>/<c>Or</c>.
    /// </summary>
    /// <param name="filter">The filter expression to parse and apply.</param>
    /// <param name="options">Optional query-string parsing options.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="filter"/> is <c>null</c>.</exception>
    public QueryOptions<TEntity> Where(string filter, QueryStringOptions? options = null)
    {
        if (filter == null)
        {
            throw new ArgumentNullException(nameof(filter));
        }

        Filter = QueryFilter.Parse<TEntity>(filter, options);
        return this;
    }

    // ---------------------------------------------------------------- shaping

    /// <summary>
    /// Eagerly loads the supplied related property paths with the query.
    /// </summary>
    /// <param name="paths">The property paths to include.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="paths"/> is <c>null</c>.</exception>
    public QueryOptions<TEntity> Include(params string[] paths)
    {
        if (paths == null)
        {
            throw new ArgumentNullException(nameof(paths));
        }

        _includePaths.UnionWith(paths);
        return this;
    }

    /// <summary>
    /// Eagerly loads a collection navigation, optionally shaped: only the related entities a filter keeps,
    /// in an order, after skipping some and up to a limit, for each parent —
    /// <c>Include(x =&gt; x.Steps, steps =&gt; steps.Where(s =&gt; s.Status != "skipped").OrderByDescending(s =&gt; s.StartedAt).Take(3))</c>.
    /// Every engine loads exactly those entities, in that order, through its own mechanism.
    /// </summary>
    /// <typeparam name="TElement">The related entity type.</typeparam>
    /// <param name="navigation">The collection navigation (<c>x =&gt; x.Steps</c>).</param>
    /// <param name="shape">The filter, order and limit of each parent's related entities, or <c>null</c> for all of them.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="navigation"/> is <c>null</c>.</exception>
    public QueryOptions<TEntity> Include<TElement>(Expression<Func<TEntity, IEnumerable<TElement>?>> navigation,
        Func<IncludeQuery<TElement>, IncludeQuery<TElement>>? shape = null)
        where TElement : class
    {
        if (navigation == null)
        {
            throw new ArgumentNullException(nameof(navigation));
        }

        var path = navigation.GetMemberName();
        _includePaths.Add(path);
        _includeShapes.Remove(path);
        if (shape is not null)
        {
            var shaped = shape(new IncludeQuery<TElement>(path))
                         ?? throw new InvalidOperationException($"The shape of the include '{path}' returned no query.");
            if (shaped.IsShaped)
            {
                _includeShapes[path] = shaped;
            }
        }

        return this;
    }

    // ---------------------------------------------------------------- ordering

    /// <summary>Appends an ascending sort by the selected member (the primary sort).</summary>
    /// <typeparam name="TKey">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    public QueryOptions<TEntity> OrderBy<TKey>(Expression<Func<TEntity, TKey>> selector) =>
        AppendSort(builder => builder.OrderBy(selector));

    /// <summary>Appends a descending sort by the selected member (the primary sort).</summary>
    /// <typeparam name="TKey">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    public QueryOptions<TEntity> OrderByDescending<TKey>(Expression<Func<TEntity, TKey>> selector) =>
        AppendSort(builder => builder.OrderByDescending(selector));

    /// <summary>Appends a descending sort by path string.</summary>
    /// <param name="path">The member path.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    public QueryOptions<TEntity> OrderByDescending(string path) =>
        AppendSort(builder => builder.OrderByDescending(path));

    /// <summary>
    /// Appends an ascending sort by the selected member, placing the rows whose member is null first or last
    /// (<c>OrderBy(x =&gt; x.Position, NullsOrder.First)</c>).
    /// </summary>
    /// <typeparam name="TKey">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="nulls">Where the rows whose member is null go.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    public QueryOptions<TEntity> OrderBy<TKey>(Expression<Func<TEntity, TKey>> selector, NullsOrder nulls) =>
        AppendSort(builder => builder.OrderBy(selector), nulls);

    /// <summary>Appends a descending sort by the selected member, placing the rows whose member is null first or last.</summary>
    /// <typeparam name="TKey">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="nulls">Where the rows whose member is null go.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    public QueryOptions<TEntity> OrderByDescending<TKey>(Expression<Func<TEntity, TKey>> selector, NullsOrder nulls) =>
        AppendSort(builder => builder.OrderByDescending(selector), nulls);

    /// <summary>Appends an ascending secondary sort by the selected member, placing the rows whose member is null first or last.</summary>
    /// <typeparam name="TKey">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="nulls">Where the rows whose member is null go.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    public QueryOptions<TEntity> ThenBy<TKey>(Expression<Func<TEntity, TKey>> selector, NullsOrder nulls) =>
        AppendSort(builder => builder.ThenBy(selector), nulls);

    /// <summary>Appends a descending secondary sort by the selected member, placing the rows whose member is null first or last.</summary>
    /// <typeparam name="TKey">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="nulls">Where the rows whose member is null go.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    public QueryOptions<TEntity> ThenByDescending<TKey>(Expression<Func<TEntity, TKey>> selector, NullsOrder nulls) =>
        AppendSort(builder => builder.ThenByDescending(selector), nulls);

    /// <summary>
    /// Where one of this query's sortings places the rows whose key is null — <see cref="NullsOrder.Default"/>
    /// unless the sort was appended with a placement.
    /// </summary>
    /// <param name="sorting">One of <see cref="Sortings"/>.</param>
    /// <returns>The placement of nulls for that sort.</returns>
    public NullsOrder NullsOrderOf(QuerySort<TEntity> sorting) =>
        sorting is not null && _nulls.TryGetValue(sorting, out var nulls) ? nulls : NullsOrder.Default;

    /// <summary>Appends an ascending secondary sort by the selected member.</summary>
    /// <typeparam name="TKey">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    public QueryOptions<TEntity> ThenBy<TKey>(Expression<Func<TEntity, TKey>> selector) =>
        AppendSort(builder => builder.ThenBy(selector));

    /// <summary>Appends an ascending secondary sort by path string.</summary>
    /// <param name="path">The member path.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    public QueryOptions<TEntity> ThenBy(string path) =>
        AppendSort(builder => builder.ThenBy(path));

    /// <summary>Appends a descending secondary sort by the selected member.</summary>
    /// <typeparam name="TKey">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    public QueryOptions<TEntity> ThenByDescending<TKey>(Expression<Func<TEntity, TKey>> selector) =>
        AppendSort(builder => builder.ThenByDescending(selector));

    /// <summary>Appends a descending secondary sort by path string.</summary>
    /// <param name="path">The member path.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    public QueryOptions<TEntity> ThenByDescending(string path) =>
        AppendSort(builder => builder.ThenByDescending(path));

    /// <summary>
    /// Appends the sortings parsed from an <c>eQuantic.Linq.Web</c> ordering
    /// expression, e.g. <c>total:desc,customer.name</c> (direction defaults to
    /// ascending), preserving their order.
    /// </summary>
    /// <param name="orderBy">The ordering expression to parse and apply.</param>
    /// <param name="options">Optional query-string parsing options.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="orderBy"/> is <c>null</c>.</exception>
    public QueryOptions<TEntity> OrderBy(string orderBy, QueryStringOptions? options = null)
    {
        if (orderBy == null)
        {
            throw new ArgumentNullException(nameof(orderBy));
        }

        _sortings.AddRange(QuerySort<TEntity>.Parse(orderBy, options));
        return this;
    }

    /// <summary>
    /// Appends the supplied sortings to the query, preserving their order.
    /// </summary>
    /// <param name="sortings">The sortings to apply.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="sortings"/> is <c>null</c>.</exception>
    public QueryOptions<TEntity> OrderBy(params QuerySort<TEntity>[] sortings)
    {
        if (sortings == null)
        {
            throw new ArgumentNullException(nameof(sortings));
        }

        _sortings.AddRange(sortings.Where(sorting => sorting != null));
        return this;
    }

    // ---------------------------------------------------------------- paging without a total

    /// <summary>
    /// Skips the first <paramref name="count"/> rows of the read, in its order. The read runs no count:
    /// pair it with an order, and with <see cref="Take"/> for a page (<c>.OrderBy(x =&gt; x.CreatedAt).Skip(10).Take(10)</c>).
    /// Reads that return rows honour it; reads over every matching row (counts, sums, groups) and the reads
    /// that page by themselves refuse it.
    /// </summary>
    /// <param name="count">How many rows to skip (zero or more).</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="count"/> is negative.</exception>
    public QueryOptions<TEntity> Skip(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "A read skips zero or more rows.");
        }

        SkipCount = count;
        return this;
    }

    /// <summary>
    /// Returns at most <paramref name="count"/> rows, after any <see cref="Skip"/>. The read runs no count.
    /// </summary>
    /// <param name="count">The limit (zero or more).</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="count"/> is negative.</exception>
    public QueryOptions<TEntity> Take(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "A read returns zero or more rows.");
        }

        TakeCount = count;
        return this;
    }

    // ---------------------------------------------------------------- behaviour

    /// <summary>
    /// Executes the query without change tracking.
    /// </summary>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    public QueryOptions<TEntity> NoTracking()
    {
        AsNoTracking = true;
        return this;
    }

    /// <summary>
    /// Ignores global query filters for this query.
    /// </summary>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    public QueryOptions<TEntity> IgnoringQueryFilters()
    {
        IgnoreQueryFilters = true;
        return this;
    }

    /// <summary>
    /// Associates a diagnostic tag with the query.
    /// </summary>
    /// <param name="tag">The tag to associate.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="tag"/> is <c>null</c>.</exception>
    public QueryOptions<TEntity> WithTag(string tag)
    {
        Tag = tag ?? throw new ArgumentNullException(nameof(tag));
        return this;
    }

    /// <summary>
    /// Applies a custom transformation to the query before filtering is applied.
    /// </summary>
    /// <param name="customize">The transformation to apply.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="customize"/> is <c>null</c>.</exception>
    public QueryOptions<TEntity> WithBeforeCustomization(Func<IQueryable<TEntity>, IQueryable<TEntity>> customize)
    {
        BeforeCustomization = customize ?? throw new ArgumentNullException(nameof(customize));
        return this;
    }

    /// <summary>
    /// Applies a custom transformation to the query after filtering and sorting are applied.
    /// </summary>
    /// <param name="customize">The transformation to apply.</param>
    /// <returns>The same <see cref="QueryOptions{TEntity}"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="customize"/> is <c>null</c>.</exception>
    public QueryOptions<TEntity> WithAfterCustomization(Func<IQueryable<TEntity>, IQueryable<TEntity>> customize)
    {
        AfterCustomization = customize ?? throw new ArgumentNullException(nameof(customize));
        return this;
    }

    // ---------------------------------------------------------------- delegation helpers

    private QueryOptions<TEntity> Fold(Action<QueryFilterBuilder<TEntity>> compose)
    {
        _filterBuilder ??= QueryFilterBuilder.For<TEntity>();
        compose(_filterBuilder);
        Filter = _filterBuilder.ToPredicate();
        return this;
    }

    private QueryOptions<TEntity> AppendSort(Func<QuerySortBuilder<TEntity>, QuerySortBuilder<TEntity>> build,
        NullsOrder nulls = NullsOrder.Default)
    {
        foreach (var sort in build(QuerySortBuilder.For<TEntity>()).ToSorts())
        {
            _sortings.Add(sort);
            if (nulls != NullsOrder.Default)
            {
                _nulls[sort] = nulls;
            }
        }

        return this;
    }
}
