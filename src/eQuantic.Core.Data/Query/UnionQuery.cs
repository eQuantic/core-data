using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using eQuantic.Linq.Expressions;

namespace eQuantic.Core.Data.Query;

/// <summary>
///     The entry point for composing a typed <c>UNION</c>/<c>UNION ALL</c>: each branch reads one entity type,
///     optionally filtered, projected into the <b>common result shape</b> — an anonymous type or a member-init
///     POCO whose members are entity members or constants (constants tag which branch a row came from).
///     Combine branches with <see cref="UnionQuery.All{TResult}" /> (keep duplicates) or
///     <see cref="UnionQuery.Distinct{TResult}" /> (SQL <c>UNION</c> semantics), and run the query on a unit of
///     work that implements <see cref="Repository.IUnionQueryRunner" />.
/// </summary>
/// <example>
///     <code>
///     var feed = UnionQuery.All(
///             Union.Of&lt;Order&gt;().Where(x =&gt; x.Status == "overdue")
///                 .Select(x =&gt; new { Name = x.Customer, Origin = "order" }),
///             Union.Of&lt;Buyer&gt;()
///                 .Select(x =&gt; new { x.Name, Origin = "buyer" }))
///         .OrderBy(row =&gt; row.Name)
///         .Take(20);
///     var rows = await unitOfWork.UnionAsync(feed);
///     </code>
/// </example>
public static class Union
{
    /// <summary>Starts a union branch over <typeparamref name="TEntity" />.</summary>
    /// <typeparam name="TEntity">The entity type the branch reads.</typeparam>
    public static UnionSource<TEntity> Of<TEntity>()
        where TEntity : class => new();
}

/// <summary>A union branch under composition: the entity source, its filters, and finally its projection.</summary>
/// <typeparam name="TEntity">The entity type the branch reads.</typeparam>
public sealed class UnionSource<TEntity>
    where TEntity : class
{
    private readonly List<LambdaExpression> _filters = [];
    private bool _ignoreQueryFilters;

    internal UnionSource()
    {
    }

    /// <summary>Filters the branch — every call ANDs another predicate. The filter applies before the union.</summary>
    /// <param name="filter">The predicate.</param>
    public UnionSource<TEntity> Where(Expression<Func<TEntity, bool>> filter)
    {
        _filters.Add(filter ?? throw new ArgumentNullException(nameof(filter)));
        return this;
    }

    /// <summary>Opts this branch out of the entity's global query filter.</summary>
    public UnionSource<TEntity> IgnoringQueryFilters()
    {
        _ignoreQueryFilters = true;
        return this;
    }

    /// <summary>
    ///     Projects the branch into the union's common shape. Each projected member must be an entity member or
    ///     a constant (a per-branch tag); derived values are computed after materialization.
    /// </summary>
    /// <typeparam name="TResult">The common result shape (anonymous or member-init).</typeparam>
    /// <param name="projection">The projection.</param>
    public UnionBranch<TResult> Select<TResult>(Expression<Func<TEntity, TResult>> projection) =>
        new(typeof(TEntity), _filters, projection ?? throw new ArgumentNullException(nameof(projection)), _ignoreQueryFilters);
}

/// <summary>One composed union branch: the entity type, its filters and its projection into the common shape.</summary>
public abstract class UnionBranch
{
    private protected UnionBranch(Type entityType, IReadOnlyList<LambdaExpression> filters, LambdaExpression projection, bool ignoreQueryFilters)
    {
        EntityType = entityType;
        Filters = filters;
        Projection = projection;
        IgnoreQueryFilters = ignoreQueryFilters;
    }

    /// <summary>The entity type the branch reads.</summary>
    public Type EntityType { get; }

    /// <summary>The branch's predicates, ANDed together (and with the entity's global filter unless opted out).</summary>
    public IReadOnlyList<LambdaExpression> Filters { get; }

    /// <summary>The projection into the common result shape.</summary>
    public LambdaExpression Projection { get; }

    /// <summary>Whether the branch opted out of the entity's global query filter.</summary>
    public bool IgnoreQueryFilters { get; }
}

/// <summary>A union branch typed by the common result shape.</summary>
/// <typeparam name="TResult">The common result shape.</typeparam>
public sealed class UnionBranch<TResult> : UnionBranch
{
    internal UnionBranch(Type entityType, IReadOnlyList<LambdaExpression> filters, LambdaExpression projection, bool ignoreQueryFilters)
        : base(entityType, filters, projection, ignoreQueryFilters)
    {
    }
}

/// <summary>Combines union branches into a runnable query.</summary>
public static class UnionQuery
{
    /// <summary>Combines the branches with <c>UNION ALL</c> — every row from every branch, duplicates kept.</summary>
    /// <typeparam name="TResult">The common result shape.</typeparam>
    /// <param name="branches">At least two branches projecting the same shape.</param>
    public static UnionQuery<TResult> All<TResult>(params UnionBranch<TResult>[] branches) => new(all: true, branches);

    /// <summary>Combines the branches with <c>UNION</c> — duplicate projected rows collapse (SQL <c>UNION</c> semantics).</summary>
    /// <typeparam name="TResult">The common result shape.</typeparam>
    /// <param name="branches">At least two branches projecting the same shape.</param>
    public static UnionQuery<TResult> Distinct<TResult>(params UnionBranch<TResult>[] branches) => new(all: false, branches);
}

/// <summary>One member of the union's combined ordering.</summary>
/// <param name="Member">The projected member's name.</param>
/// <param name="Descending">Whether the member orders descending.</param>
public sealed record UnionOrder(string Member, bool Descending);

/// <summary>A runnable union: the branches, the combine mode, and the combined ordering and paging.</summary>
/// <typeparam name="TResult">The common result shape.</typeparam>
public sealed class UnionQuery<TResult>
{
    private readonly List<UnionOrder> _order = [];

    internal UnionQuery(bool all, IReadOnlyList<UnionBranch<TResult>> branches)
    {
        if (branches is null || branches.Count < 2)
        {
            throw new ArgumentException("A union needs at least two branches.", nameof(branches));
        }

        All = all;
        Branches = branches;
    }

    /// <summary>Whether duplicates are kept (<c>UNION ALL</c>) or collapsed (<c>UNION</c>).</summary>
    public bool All { get; }

    /// <summary>The branches, in order.</summary>
    public IReadOnlyList<UnionBranch<TResult>> Branches { get; }

    /// <summary>The combined ordering over projected members, in order.</summary>
    public IReadOnlyList<UnionOrder> Order => _order;

    /// <summary>The maximum number of combined rows, when set.</summary>
    public int? Limit { get; private set; }

    /// <summary>The number of combined rows skipped, when set.</summary>
    public int? Offset { get; private set; }

    /// <summary>Orders the combined result by a projected member, ascending.</summary>
    /// <param name="member">The projected member.</param>
    public UnionQuery<TResult> OrderBy(Expression<Func<TResult, object?>> member)
    {
        _order.Add(new UnionOrder(member.GetMemberName(), Descending: false));
        return this;
    }

    /// <summary>Orders the combined result by a projected member, descending.</summary>
    /// <param name="member">The projected member.</param>
    public UnionQuery<TResult> OrderByDescending(Expression<Func<TResult, object?>> member)
    {
        _order.Add(new UnionOrder(member.GetMemberName(), Descending: true));
        return this;
    }

    /// <summary>Limits the combined result to <paramref name="count" /> rows — applied after the union, on the store.</summary>
    /// <param name="count">The maximum number of rows.</param>
    public UnionQuery<TResult> Take(int count)
    {
        Limit = count >= 1 ? count : throw new ArgumentOutOfRangeException(nameof(count), count, "Take needs at least 1 row.");
        return this;
    }

    /// <summary>Skips <paramref name="count" /> combined rows — applied after the union, on the store, with <see cref="Take" />.</summary>
    /// <param name="count">The number of rows skipped.</param>
    public UnionQuery<TResult> Skip(int count)
    {
        Offset = count >= 0 ? count : throw new ArgumentOutOfRangeException(nameof(count), count, "Skip cannot be negative.");
        return this;
    }
}
