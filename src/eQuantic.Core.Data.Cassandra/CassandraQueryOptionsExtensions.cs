using System.Linq.Expressions;
using eQuantic.Core.Data.Query;
using eQuantic.Core.Data.Repository.Options;

namespace eQuantic.Core.Data.Cassandra;

/// <summary>Cassandra-specific query-option opt-ins.</summary>
public static class CassandraQueryOptionsExtensions
{
    internal const string AllowFilteringTag = "cassandra:allow-filtering";

    /// <summary>
    ///     Opts this query into <c>ALLOW FILTERING</c>, letting it filter on non-key columns. This is a scan —
    ///     use it deliberately; the default (without this) rejects non-key filters so accidental scans do not ship.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="options">The query options.</param>
    /// <returns>The same options for chaining.</returns>
    public static QueryOptions<TEntity> AllowFiltering<TEntity>(this QueryOptions<TEntity> options)
        where TEntity : class => options.WithTag(AllowFilteringTag);
}

/// <summary>Builds CQL <c>WHERE</c> fragments from a <see cref="QueryOptions{TEntity}" /> via the filter translator.</summary>
internal static class CassandraCql
{
    public static (string Where, object?[] Values, bool RequiresFiltering) Where<TEntity>(
        CassandraEntityConfiguration configuration, QueryOptions<TEntity>? options)
        where TEntity : class
    {
        var clauses = new List<string>();
        var values = new List<object?>();
        var requiresFiltering = false;

        foreach (var filter in Filters(options))
        {
            var (cql, filterValues, allowFiltering) = CassandraCqlRenderer.Render(configuration, FilterInterpreter.Interpret(filter));
            if (cql.Length == 0)
            {
                continue;
            }

            clauses.Add(cql);
            values.AddRange(filterValues);
            requiresFiltering |= allowFiltering;
        }

        return (string.Join(" AND ", clauses), values.ToArray(), requiresFiltering);
    }

    public static bool AllowFilteringOptedIn<TEntity>(QueryOptions<TEntity>? options) where TEntity : class =>
        options?.Tag == CassandraQueryOptionsExtensions.AllowFilteringTag;

    private static IEnumerable<Expression<Func<TEntity, bool>>> Filters<TEntity>(QueryOptions<TEntity>? options) where TEntity : class
    {
        if (options?.Filter is not null)
        {
            yield return options.Filter;
        }

        if (options?.Specification is not null)
        {
            yield return options.Specification.SatisfiedBy();
        }
    }
}
