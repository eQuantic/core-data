using System.Linq.Expressions;
using eQuantic.Linq.Expressions;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Linq.Web;

namespace eQuantic.Core.Data.CosmosDb;

/// <summary>
///     Applies a <see cref="QueryOptions{TEntity}" /> to a Cosmos <see cref="IQueryable{T}" /> obtained from
///     <c>Container.GetItemLinqQueryable&lt;TEntity&gt;()</c>, which the SDK translates to a SQL query.
/// </summary>
/// <remarks>
///     The store-agnostic shaping — before/after customization, specification + predicate filter and sortings —
///     is applied here. <c>Include</c> is rejected by the repository (Cosmos has no cross-document navigations);
///     the remaining EF-only knobs (change tracking, global query filters, query tags) have no meaning for a
///     document store and are intentionally ignored.
/// </remarks>
public static class CosmosQueryableExtensions
{
    /// <summary>
    ///     Shapes the query with the supplied options. An enum stored as its labels — declared on the model with
    ///     <c>Enum&lt;TEnum&gt;()</c> or annotated with <c>[EnumType]</c>, as the client's serializer says — compares by its
    ///     members' values, as C# does: an ordered comparison in the filter becomes membership in the labels whose member
    ///     satisfies it. An order by such a member is refused: an <c>ORDER BY</c> would order the labels' text, and Cosmos DB
    ///     orders by property paths alone, so one query cannot say it; the repository reads one query per member instead.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="source">The source query (typically the container).</param>
    /// <param name="options">The query options, or <c>null</c> for no shaping.</param>
    /// <returns>The shaped query.</returns>
    /// <exception cref="NotSupportedException">An order by a member stored as an enum's labels, or by an enum when the query's serializer cannot be read.</exception>
    public static IQueryable<TEntity> ApplyQueryOptions<TEntity>(this IQueryable<TEntity> source,
        QueryOptions<TEntity>? options)
        where TEntity : class
    {
        if (options is null)
        {
            return source;
        }

        var labels = Labels.Of(source);
        foreach (var sort in options.Sortings.Where(sort => sort is not null))
        {
            var key = Unconverted(sort.KeySelector.Body).Type;
            var type = Nullable.GetUnderlyingType(key) ?? key;
            if (type.IsEnum && labels.Orders(type) is { } refusal)
            {
                throw new NotSupportedException($"'{sort.KeySelector.GetMemberName()}' {refusal}");
            }
        }

        var shaped = source.ApplyQueryOptions(options, nullsPlaced: false);
        return labels.IsLabelEnum is { } isLabelEnum && !ReferenceEquals(shaped, source)
            ? shaped.Provider.CreateQuery<TEntity>(eQuantic.Core.Data.Query.EnumOrder.RewriteComparisons(shaped.Expression, isLabelEnum))
            : shaped;
    }

    private static Expression Unconverted(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } conversion)
        {
            expression = conversion.Operand;
        }

        return expression;
    }

    /// <summary>
    ///     What a query knows about the enums its store keeps as labels: the client's serializer when the query is the SDK's
    ///     (the provider's container names the client), nothing to know when it is not (a query in memory compares enums
    ///     as C# does), and "cannot tell" when it is the SDK's and its container cannot be read.
    /// </summary>
    private sealed class Labels
    {
        private readonly bool _unknown;

        private Labels(Func<Type, bool>? isLabelEnum, bool unknown)
        {
            IsLabelEnum = isLabelEnum;
            _unknown = unknown;
        }

        /// <summary>Whether the store keeps an enum as its labels, or <c>null</c> when no store translates the query.</summary>
        public Func<Type, bool>? IsLabelEnum { get; }

        public static Labels Of(IQueryable source)
        {
            var provider = source.Provider;
            if (provider.GetType().Namespace?.StartsWith("Microsoft.Azure.Cosmos", StringComparison.Ordinal) != true)
            {
                return new Labels(null, unknown: false);
            }

            var container = provider.GetType()
                .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .Where(field => typeof(Microsoft.Azure.Cosmos.Container).IsAssignableFrom(field.FieldType))
                .Select(field => field.GetValue(provider) as Microsoft.Azure.Cosmos.Container)
                .FirstOrDefault(found => found is not null);
            if (container is null)
            {
                return new Labels(type => Data.Modeling.EnumTypeAttribute.For(Nullable.GetUnderlyingType(type) ?? type) is not null, unknown: true);
            }

            return container.Database.Client.ClientOptions.Serializer is CosmosEntitySerializer serializer
                ? new Labels(serializer.IsLabelEnum, unknown: false)
                : new Labels(_ => false, unknown: false);
        }

        /// <summary>Why an order by <paramref name="enumType" /> cannot run, or <c>null</c> when it can.</summary>
        public string? Orders(Type enumType)
        {
            if (_unknown)
            {
                return $"is an enum, and the query's serializer cannot be read to tell whether Cosmos DB stores '{enumType.Name}' as its " +
                       "labels (which an ORDER BY would order by their text); read through the repository, which knows the model.";
            }

            return IsLabelEnum?.Invoke(enumType) == true
                ? $"is stored as the labels of '{enumType.Name}', which an ORDER BY would order by their text while C# orders the " +
                  "members' values, and Cosmos DB orders by property paths alone; read through the repository (GetAllAsync, " +
                  "GetPagedAsync), which reads one query per member in the order of their values."
                : null;
        }
    }

    /// <summary>
    ///     Shapes the query; the repository passes <paramref name="nullsPlaced" /> when it places nulls itself (two
    ///     queries split on whether the first key is null). Anyone else asking for a placement against Cosmos DB's order
    ///     is refused: a <c>ORDER BY</c> cannot say it.
    /// </summary>
    internal static IQueryable<TEntity> ApplyQueryOptions<TEntity>(this IQueryable<TEntity> source,
        QueryOptions<TEntity>? options, bool nullsPlaced)
        where TEntity : class
    {
        if (options is null)
        {
            return source;
        }

        if (!nullsPlaced && options.Sortings.Any(sort =>
                !eQuantic.Core.Data.Query.ReadShapes.IsNullsSmallest(options.NullsOrderOf(sort),
                    sort.Direction == SortDirection.Descending)))
        {
            throw new NotSupportedException(
                "Cosmos DB sorts nulls and undefined as the smallest value, and an ORDER BY cannot place them otherwise; " +
                "read through the repository, which splits the read on whether the key is null.");
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
                [typeof(TEntity), sort.KeySelector.ReturnType],
                query.Expression,
                Expression.Quote(sort.KeySelector));

            query = query.Provider.CreateQuery<TEntity>(call);
            first = false;
        }

        return query;
    }
}
