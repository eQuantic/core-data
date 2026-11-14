using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace eQuantic.Core.Data.Repository.Options;

/// <summary>One sort of an included collection: the element member, the direction and where nulls go.</summary>
/// <param name="KeySelector">The element member to sort by (<c>s =&gt; s.StartedAt</c>).</param>
/// <param name="Descending">Whether the sort is descending.</param>
/// <param name="Nulls">Where the elements whose key is null go.</param>
public sealed record IncludeSort(LambdaExpression KeySelector, bool Descending, NullsOrder Nulls);

/// <summary>
///     What an include of a collection loads for each parent: the related entities a filter keeps, in an order,
///     after skipping some and up to a limit. The provider reads it and loads exactly those, in that order, for
///     each parent — through its own mechanism (a window function, a <c>$lookup</c> pipeline, a second query).
/// </summary>
public abstract class IncludeShape
{
    private protected readonly List<IncludeSort> SortList = [];

    private protected IncludeShape(string path, Type elementType)
    {
        Path = path;
        ElementType = elementType;
    }

    /// <summary>The navigation the shape applies to (a collection member of the parent).</summary>
    public string Path { get; }

    /// <summary>The type of the related entities.</summary>
    public Type ElementType { get; }

    /// <summary>The filter the related entities must match (<c>Func&lt;TElement, bool&gt;</c>), or <c>null</c> for all of them.</summary>
    public LambdaExpression? Filter { get; private protected set; }

    /// <summary>The order of the related entities of each parent, primary sort first.</summary>
    public IReadOnlyList<IncludeSort> Sortings => SortList;

    /// <summary>How many related entities of each parent to skip, in the shape's order, or <c>null</c>.</summary>
    public int? SkipCount { get; private protected set; }

    /// <summary>How many related entities of each parent to load at most, or <c>null</c> for all of them.</summary>
    public int? TakeCount { get; private protected set; }

    /// <summary>Whether the shape asks for anything beyond loading every related entity.</summary>
    public bool IsShaped => Filter is not null || SortList.Count > 0 || SkipCount is not null || TakeCount is not null;
}

/// <summary>
///     Shapes an included collection, read the way a LINQ query over it reads:
///     <c>steps =&gt; steps.Where(s =&gt; s.Status != "skipped").OrderByDescending(s =&gt; s.StartedAt).Take(3)</c>.
///     Every call returns the same instance; <c>Where</c> twice means both, <c>OrderBy</c> and <c>ThenBy</c> append
///     sorts in order, and <c>Skip</c>/<c>Take</c> apply per parent, after the order.
/// </summary>
/// <typeparam name="TElement">The related entity type.</typeparam>
public sealed class IncludeQuery<TElement> : IncludeShape
    where TElement : class
{
    internal IncludeQuery(string path) : base(path, typeof(TElement))
    {
    }

    /// <summary>Keeps only the related entities matching <paramref name="filter" /> (ANDed with any earlier filter).</summary>
    /// <param name="filter">The filter over the related entity.</param>
    /// <returns>The same shape for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="filter" /> is <c>null</c>.</exception>
    public IncludeQuery<TElement> Where(Expression<Func<TElement, bool>> filter)
    {
        if (filter == null)
        {
            throw new ArgumentNullException(nameof(filter));
        }

        if (Filter is Expression<Func<TElement, bool>> previous)
        {
            var parameter = previous.Parameters[0];
            var body = new ParameterSwap(filter.Parameters[0], parameter).Visit(filter.Body);
            Filter = Expression.Lambda<Func<TElement, bool>>(Expression.AndAlso(previous.Body, body), parameter);
        }
        else
        {
            Filter = filter;
        }

        return this;
    }

    /// <summary>Appends an ascending sort by the selected member.</summary>
    /// <typeparam name="TKey">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="nulls">Where the elements whose member is null go.</param>
    /// <returns>The same shape for chaining.</returns>
    public IncludeQuery<TElement> OrderBy<TKey>(Expression<Func<TElement, TKey>> selector, NullsOrder nulls = NullsOrder.Default) =>
        Sort(selector, false, nulls);

    /// <summary>Appends a descending sort by the selected member.</summary>
    /// <typeparam name="TKey">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="nulls">Where the elements whose member is null go.</param>
    /// <returns>The same shape for chaining.</returns>
    public IncludeQuery<TElement> OrderByDescending<TKey>(Expression<Func<TElement, TKey>> selector, NullsOrder nulls = NullsOrder.Default) =>
        Sort(selector, true, nulls);

    /// <summary>Appends an ascending secondary sort by the selected member.</summary>
    /// <typeparam name="TKey">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="nulls">Where the elements whose member is null go.</param>
    /// <returns>The same shape for chaining.</returns>
    public IncludeQuery<TElement> ThenBy<TKey>(Expression<Func<TElement, TKey>> selector, NullsOrder nulls = NullsOrder.Default) =>
        Sort(selector, false, nulls);

    /// <summary>Appends a descending secondary sort by the selected member.</summary>
    /// <typeparam name="TKey">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="nulls">Where the elements whose member is null go.</param>
    /// <returns>The same shape for chaining.</returns>
    public IncludeQuery<TElement> ThenByDescending<TKey>(Expression<Func<TElement, TKey>> selector, NullsOrder nulls = NullsOrder.Default) =>
        Sort(selector, true, nulls);

    /// <summary>Skips the first <paramref name="count" /> related entities of each parent, in the shape's order.</summary>
    /// <param name="count">How many to skip (zero or more).</param>
    /// <returns>The same shape for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="count" /> is negative.</exception>
    public IncludeQuery<TElement> Skip(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "An include skips zero or more related entities.");
        }

        SkipCount = count;
        return this;
    }

    /// <summary>Loads at most <paramref name="count" /> related entities of each parent, in the shape's order.</summary>
    /// <param name="count">The limit (zero or more).</param>
    /// <returns>The same shape for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="count" /> is negative.</exception>
    public IncludeQuery<TElement> Take(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "An include takes zero or more related entities.");
        }

        TakeCount = count;
        return this;
    }

    private IncludeQuery<TElement> Sort(LambdaExpression selector, bool descending, NullsOrder nulls)
    {
        if (selector == null)
        {
            throw new ArgumentNullException(nameof(selector));
        }

        SortList.Add(new IncludeSort(selector, descending, nulls));
        return this;
    }

    private sealed class ParameterSwap(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
