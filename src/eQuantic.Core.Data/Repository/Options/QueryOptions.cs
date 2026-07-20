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
    private readonly List<QuerySort<TEntity>> _sortings = new();
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
    /// Gets the ordered set of sortings applied to the query.
    /// </summary>
    public IReadOnlyList<QuerySort<TEntity>> Sortings => _sortings;

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

    private QueryOptions<TEntity> AppendSort(Func<QuerySortBuilder<TEntity>, QuerySortBuilder<TEntity>> build)
    {
        _sortings.AddRange(build(QuerySortBuilder.For<TEntity>()).ToSorts());
        return this;
    }
}
