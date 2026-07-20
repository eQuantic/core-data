using System.Linq.Expressions;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Linq.Web;

namespace eQuantic.Core.Data.MongoDb;

/// <summary>
///     Applies a <see cref="QueryOptions{TEntity}" /> to a MongoDB <see cref="IQueryable{T}" /> (obtained
///     from <c>IMongoCollection&lt;TEntity&gt;.AsQueryable()</c>, which is an <c>IMongoQueryable</c> and thus
///     a real <see cref="IQueryable{T}" /> that the driver translates to an aggregation pipeline).
/// </summary>
/// <remarks>
///     The store-agnostic shaping — before/after customization, specification + predicate filter and
///     sortings — is applied here. The EF-only knobs (<c>Include</c>, change tracking, global query filters,
///     query tags) have no meaning for a document store and are intentionally ignored.
/// </remarks>
public static class MongoQueryableExtensions
{
    /// <summary>Shapes the query with the supplied options.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="source">The source query (typically the collection).</param>
    /// <param name="options">The query options, or <c>null</c> for no shaping.</param>
    /// <returns>The shaped query.</returns>
    public static IQueryable<TEntity> ApplyQueryOptions<TEntity>(this IQueryable<TEntity> source,
        QueryOptions<TEntity>? options)
        where TEntity : class
    {
        if (options is null)
        {
            return source;
        }

        var query = source;

        if (options.BeforeCustomization is not null)
        {
            query = options.BeforeCustomization(query);
        }

        if (options.Specification is not null)
        {
            query = query.Where(options.Specification.SatisfiedBy());
        }

        if (options.Filter is not null)
        {
            query = query.Where(options.Filter);
        }

        if (options.Sortings.Count > 0)
        {
            query = ApplySortings(query, options.Sortings);
        }

        if (options.AfterCustomization is not null)
        {
            query = options.AfterCustomization(query);
        }

        return query;
    }

    private static IQueryable<TEntity> ApplySortings<TEntity>(IQueryable<TEntity> query,
        IReadOnlyList<QuerySort<TEntity>> sortings)
    {
        var first = true;
        foreach (var sort in sortings)
        {
            if (sort is null)
            {
                continue;
            }

            var method = first
                ? (sort.Direction == SortDirection.Ascending ? nameof(Queryable.OrderBy) : nameof(Queryable.OrderByDescending))
                : (sort.Direction == SortDirection.Ascending ? nameof(Queryable.ThenBy) : nameof(Queryable.ThenByDescending));

            var call = Expression.Call(
                typeof(Queryable),
                method,
                new[] { typeof(TEntity), sort.KeySelector.ReturnType },
                query.Expression,
                Expression.Quote(sort.KeySelector));

            query = query.Provider.CreateQuery<TEntity>(call);
            first = false;
        }

        return query;
    }
}
