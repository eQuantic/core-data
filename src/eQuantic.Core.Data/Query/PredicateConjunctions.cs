using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace eQuantic.Core.Data.Query;

/// <summary>
///     Splits a predicate into its top-level conjuncts (<c>A &amp;&amp; B &amp;&amp; C</c> → <c>[A, B, C]</c>).
///     This is the classic pushdown rule query engines apply: a conjunction distributes safely — each conjunct can
///     be evaluated independently (some pushed to the store, the rest evaluated client-side) and the results
///     intersected — while a disjunction cannot be split, so an <c>OR</c> stays one conjunct.
/// </summary>
public static class PredicateConjunctions
{
    /// <summary>Splits the predicate into its flattened top-level conjuncts, preserving order.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="predicate">The predicate to split.</param>
    /// <returns>The conjuncts, each a predicate over the same parameter; the predicate itself when it has no top-level AND.</returns>
    public static IReadOnlyList<Expression<Func<TEntity, bool>>> Split<TEntity>(Expression<Func<TEntity, bool>> predicate)
    {
        if (predicate is null)
        {
            throw new ArgumentNullException(nameof(predicate));
        }

        var conjuncts = new List<Expression<Func<TEntity, bool>>>();
        Flatten(predicate.Body, predicate.Parameters[0], conjuncts);
        return conjuncts;
    }

    private static void Flatten<TEntity>(Expression body, ParameterExpression parameter,
        List<Expression<Func<TEntity, bool>>> conjuncts)
    {
        if (body is BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.And } and { } binary
            && binary.Type == typeof(bool))
        {
            Flatten(binary.Left, parameter, conjuncts);
            Flatten(binary.Right, parameter, conjuncts);
            return;
        }

        conjuncts.Add(Expression.Lambda<Func<TEntity, bool>>(body, parameter));
    }
}

/// <summary>
///     Splits a predicate into its top-level disjuncts (<c>A || B || C</c> → <c>[A, B, C]</c>). A disjunction
///     cannot be half-pushed, but it can be <b>split</b>: when every branch is independently expressible, the
///     query runs once per branch and the union of the results (de-duplicated) is the answer — the pattern
///     key-value stores model as "one query per access path".
/// </summary>
public static class PredicateDisjunctions
{
    /// <summary>Splits the predicate into its flattened top-level disjuncts, preserving order.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="predicate">The predicate to split.</param>
    /// <returns>The disjuncts; the predicate itself when it has no top-level OR.</returns>
    public static IReadOnlyList<Expression<Func<TEntity, bool>>> Split<TEntity>(Expression<Func<TEntity, bool>> predicate)
    {
        if (predicate is null)
        {
            throw new ArgumentNullException(nameof(predicate));
        }

        var disjuncts = new List<Expression<Func<TEntity, bool>>>();
        Flatten(predicate.Body, predicate.Parameters[0], disjuncts);
        return disjuncts;
    }

    private static void Flatten<TEntity>(Expression body, ParameterExpression parameter,
        List<Expression<Func<TEntity, bool>>> disjuncts)
    {
        if (body is BinaryExpression { NodeType: ExpressionType.OrElse or ExpressionType.Or } and { } binary
            && binary.Type == typeof(bool))
        {
            Flatten(binary.Left, parameter, disjuncts);
            Flatten(binary.Right, parameter, disjuncts);
            return;
        }

        disjuncts.Add(Expression.Lambda<Func<TEntity, bool>>(body, parameter));
    }
}
