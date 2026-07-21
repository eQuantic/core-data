using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using eQuantic.Linq.Expressions;
using eQuantic.Linq.Expressions.Nodes;

namespace eQuantic.Core.Data.Query;

/// <summary>
///     Shared evaluation over the eQuantic.Linq.Expressions node model, used by the filter and update
///     interpreters. The partial evaluator has already folded captured variables into constants; what stays
///     structural but parameter-free (an inline <c>new DateTime(...)</c>, <c>Guid.Parse(...)</c>,
///     <c>DateTime.UtcNow</c>) is rebuilt through the serializer and evaluated — the same folding LINQ providers
///     apply at translation time. A subtree still referencing the lambda parameter cannot compile as a value and
///     reports failure, which is how callers detect entity-referencing operands without a bespoke node walker.
/// </summary>
internal static class NodeEvaluation
{
    /// <summary>The shared serializer: its reflection caches make repeated conversion cheap, and it is thread-safe.</summary>
    public static ExpressionSerializer Serializer { get; } = new();

    /// <summary>Strips compiler-inserted conversions so a member/constant underneath is reached.</summary>
    public static ExpressionNode Unwrap(ExpressionNode node) => node switch
    {
        // Casts (Convert) and conversion operators such as the array -> ReadOnlySpan op_Implicit that binds
        // `array.Contains(x)` to MemoryExtensions.Contains (its ByRefLike node stays structural after folding).
        UnaryNode { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked, Operand: { } operand } => Unwrap(operand),
        MethodCallNode { Object: null, Method.Name: "op_Implicit" or "op_Explicit", Arguments: [var argument] } => Unwrap(argument),
        _ => node,
    };

    /// <summary>Attempts to evaluate the node as a parameter-free value.</summary>
    public static bool TryValue(ExpressionNode node, out object? value)
    {
        node = Unwrap(node);
        if (node is ConstantNode constant)
        {
            value = constant.Value;
            return true;
        }

        try
        {
            value = Expression.Lambda(Serializer.ToExpression(node)).Compile().DynamicInvoke();
            return true;
        }
        catch
        {
            value = null;
            return false;
        }
    }

    /// <summary>Attempts to evaluate the node as a parameter-free sequence of values.</summary>
    public static bool TryValues(ExpressionNode node, out IReadOnlyList<object?> values)
    {
        switch (Unwrap(node))
        {
            case ConstantNode constant when constant.Value is IEnumerable sequence and not string:
                values = sequence.Cast<object?>().ToList();
                return true;
            case NewArrayNode { Expressions: { } elements }:
            {
                var items = new List<object?>(elements.Count);
                foreach (var element in elements)
                {
                    if (!TryValue(element, out var item))
                    {
                        values = [];
                        return false;
                    }

                    items.Add(item);
                }

                values = items;
                return true;
            }
            case var other when TryValue(other, out var evaluated) && evaluated is IEnumerable sequence and not string:
                values = sequence.Cast<object?>().ToList();
                return true;
            default:
                values = [];
                return false;
        }
    }
}
