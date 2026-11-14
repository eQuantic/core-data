using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using eQuantic.Core.Data.Repository.Options;

namespace eQuantic.Core.Data.Query;

/// <summary>One sort a provider applies in memory: the key, its direction and where nulls go.</summary>
/// <param name="Key">The member selector over the sorted type.</param>
/// <param name="Descending">Whether the sort is descending.</param>
/// <param name="Nulls">Where the rows whose key is null go.</param>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed record MemorySort(LambdaExpression Key, bool Descending, NullsOrder Nulls);

/// <summary>
///     The rules every provider applies to the read shapes the same way, so that what a read means does not depend
///     on the store: which reads take <c>Skip</c>/<c>Take</c>, how they combine with a read's own limit, and how a
///     store that has to order rows itself (a related collection gathered from a second query, a Cassandra column
///     that is not a clustering key) orders them as the stores do.
/// </summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class ReadShapes
{
    /// <summary>
    ///     Refuses <c>Skip</c>/<c>Take</c> on a read that works over every matching row (a count, a sum, a group) or
    ///     that pages by itself: silently counting a page, or paging twice, would answer another question.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="options">The read's options.</param>
    /// <param name="read">The read, as the message names it (<c>CountAsync</c>).</param>
    public static void RefuseRowWindow<TEntity>(QueryOptions<TEntity>? options, string read) where TEntity : class
    {
        if (options is { SkipCount: not null } or { TakeCount: not null })
        {
            throw new NotSupportedException(
                $"Skip and Take shape the rows a read returns, and {read} does not return them that way; drop them from " +
                "the options (GetPagedAsync reads a page with its total, GetPageAsync pages by continuation).");
        }
    }

    /// <summary>Refuses what a distinct read cannot honour: an order, a skip or a limit.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="options">The read's options.</param>
    public static void RefuseForDistinct<TEntity>(QueryOptions<TEntity>? options) where TEntity : class
    {
        if (options is { Sortings.Count: > 0 })
        {
            throw new NotSupportedException(
                "GetDistinctAsync returns the distinct values in no particular order; drop the sorting and order the values client-side.");
        }

        RefuseRowWindow(options, "GetDistinctAsync");

        if (options is { IncludePaths.Count: > 0 })
        {
            throw new NotSupportedException("Include does not apply to distinct values; drop the includes.");
        }
    }

    /// <summary>
    ///     The window a read returns: the options' <c>Skip</c> as the offset, and the smaller of the options'
    ///     <c>Take</c> and the read's own limit (one row for a first, two for a single) as the limit.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="options">The read's options.</param>
    /// <param name="limit">The read's own limit, or <c>null</c>.</param>
    /// <returns>The offset and the limit, each <c>null</c> when absent.</returns>
    public static (int? Offset, int? Limit) Window<TEntity>(QueryOptions<TEntity>? options, int? limit) where TEntity : class
    {
        var take = options?.TakeCount;
        var effective = take is null ? limit : limit is null ? take : Math.Min(take.Value, limit.Value);
        var skip = options?.SkipCount is > 0 ? options.SkipCount : null;
        return (skip, effective);
    }

    /// <summary>The sorts of the options with where each places its nulls.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="options">The read's options.</param>
    public static IReadOnlyList<MemorySort> Sorts<TEntity>(QueryOptions<TEntity>? options) where TEntity : class =>
        options is null
            ? []
            : options.Sortings.Where(sort => sort is not null)
                .Select(sort => new MemorySort(sort.KeySelector, sort.Direction == eQuantic.Linq.Web.SortDirection.Descending,
                    options.NullsOrderOf(sort)))
                .ToList();

    /// <summary>The sorts of an include's shape.</summary>
    /// <param name="shape">The include's shape.</param>
    public static IReadOnlyList<MemorySort> Sorts(IncludeShape shape) =>
        shape.Sortings.Select(sort => new MemorySort(sort.KeySelector, sort.Descending, sort.Nulls)).ToList();

    /// <summary>
    ///     Whether a placement is the one a store whose nulls sort as the smallest value gives anyway (MongoDB and
    ///     Cosmos DB: first ascending, last descending).
    /// </summary>
    /// <param name="nulls">The placement asked for.</param>
    /// <param name="descending">The direction of the sort.</param>
    public static bool IsNullsSmallest(NullsOrder nulls, bool descending) =>
        nulls == NullsOrder.Default || nulls == (descending ? NullsOrder.Last : NullsOrder.First);

    /// <summary>
    ///     Orders rows in memory as a store does: text compares ordinally, the rest by its own ordering, and nulls go
    ///     where each sort says (first when ascending and last when descending by default, as nulls sort as the
    ///     smallest value). The sort is stable.
    /// </summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="rows">The rows.</param>
    /// <param name="sorts">The sorts, primary first.</param>
    public static IEnumerable<T> Order<T>(IEnumerable<T> rows, IReadOnlyList<MemorySort> sorts)
    {
        if (sorts.Count == 0)
        {
            return rows;
        }

        var keys = sorts.Select(sort => Boxed<T>(sort.Key)).ToArray();
        return rows
            .Select(row => (Row: row, Keys: keys.Select(key => key(row)).ToArray()))
            .OrderBy(entry => entry.Keys, new KeysComparer(sorts))
            .Select(entry => entry.Row);
    }

    /// <summary>Applies an include's order, skip and limit to one parent's related rows (already filtered).</summary>
    /// <typeparam name="T">The related row type.</typeparam>
    /// <param name="rows">One parent's related rows.</param>
    /// <param name="shape">The include's shape.</param>
    public static List<T> Page<T>(IEnumerable<T> rows, IncludeShape shape)
    {
        var ordered = Order(rows, Sorts(shape));
        if (shape.SkipCount is > 0)
        {
            ordered = ordered.Skip(shape.SkipCount.Value);
        }

        if (shape.TakeCount is { } take)
        {
            ordered = ordered.Take(take);
        }

        return ordered.ToList();
    }

    // A sort key compiles once per key expression (once per shape, however many parents it pages), not once per call.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LambdaExpression,
        System.Collections.Concurrent.ConcurrentDictionary<Type, Delegate>> Compiled = new();

    private static Func<T, object?> Boxed<T>(LambdaExpression key) =>
        (Func<T, object?>)Compiled.GetValue(key, static _ => new System.Collections.Concurrent.ConcurrentDictionary<Type, Delegate>())
            .GetOrAdd(typeof(T), static (_, key) => Compile<T>(key), key);

    private static Func<T, object?> Compile<T>(LambdaExpression key)
    {
        var parameter = Expression.Parameter(typeof(T), "row");
        var body = new Rebind(key.Parameters[0], parameter.Type == key.Parameters[0].Type
            ? parameter
            : Expression.Convert(parameter, key.Parameters[0].Type)).Visit(key.Body);
        return Expression.Lambda<Func<T, object?>>(Expression.Convert(body, typeof(object)), parameter).Compile();
    }

    private sealed class Rebind(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }

    private sealed class KeysComparer(IReadOnlyList<MemorySort> sorts) : IComparer<object?[]>
    {
        public int Compare(object?[]? x, object?[]? y)
        {
            for (var index = 0; index < sorts.Count; index++)
            {
                var sort = sorts[index];
                var left = x![index];
                var right = y![index];
                if (left is null || right is null)
                {
                    if (left is null && right is null)
                    {
                        continue;
                    }

                    // Nulls are placed independently of the direction.
                    var nullsFirst = sort.Nulls switch
                    {
                        NullsOrder.First => true,
                        NullsOrder.Last => false,
                        _ => !sort.Descending,
                    };
                    return (left is null) == nullsFirst ? -1 : 1;
                }

                var compared = left is string text && right is string other
                    ? string.CompareOrdinal(text, other)
                    : Comparer<object>.Default.Compare(left, right);
                if (compared != 0)
                {
                    return sort.Descending ? -compared : compared;
                }
            }

            return 0;
        }
    }
}
