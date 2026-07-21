using System.Collections.Generic;
using System.Linq.Expressions;
using eQuantic.Linq.Expressions.Nodes;

namespace eQuantic.Core.Data.Query;

/// <summary>
///     Splits a predicate — in its eQuantic.Linq.Expressions node form — into its top-level conjuncts or
///     disjuncts. Working on the node model means the predicate is split <b>after</b> partial evaluation and
///     normalization, in the same single pass the interpreters consume: <c>A &amp;&amp; B</c> distributes safely
///     (each conjunct can run on a different side — pushed down or residual), while an <c>OR</c> cannot be
///     half-pushed but can be split into one native query per branch when every branch is expressible.
/// </summary>
public static class NodePredicates
{
    /// <summary>The flattened top-level conjuncts of the body (<c>A &amp;&amp; B &amp;&amp; C</c> → <c>[A, B, C]</c>), in order.</summary>
    /// <param name="body">The predicate body node.</param>
    public static IReadOnlyList<ExpressionNode> Conjuncts(ExpressionNode body)
    {
        var conjuncts = new List<ExpressionNode>();
        Flatten(body, ExpressionType.AndAlso, ExpressionType.And, conjuncts);
        return conjuncts;
    }

    /// <summary>The flattened top-level disjuncts of the body (<c>A || B || C</c> → <c>[A, B, C]</c>), in order.</summary>
    /// <param name="body">The predicate body node.</param>
    public static IReadOnlyList<ExpressionNode> Disjuncts(ExpressionNode body)
    {
        var disjuncts = new List<ExpressionNode>();
        Flatten(body, ExpressionType.OrElse, ExpressionType.Or, disjuncts);
        return disjuncts;
    }

    private static void Flatten(ExpressionNode node, ExpressionType shortCircuit, ExpressionType eager, List<ExpressionNode> parts)
    {
        if (node is BinaryNode binary && (binary.NodeType == shortCircuit || binary.NodeType == eager))
        {
            Flatten(binary.Left, shortCircuit, eager, parts);
            Flatten(binary.Right, shortCircuit, eager, parts);
            return;
        }

        parts.Add(node);
    }
}
