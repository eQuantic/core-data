using System;
using System.ComponentModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace eQuantic.Core.Data.Query;

/// <summary>
///     Rewrites the shapes a newer C# compiler gives an expression into the ones every LINQ provider reads, before an
///     expression is handed to a driver or compiled for client-side evaluation. Under C# 14, <c>array.Contains(x.Member)</c>
///     binds to <c>MemoryExtensions.Contains</c> over a <c>ReadOnlySpan&lt;T&gt;</c> (through an <c>op_Implicit</c>
///     conversion), which a driver's translator does not know and an expression compiler cannot always run; it becomes
///     <c>Enumerable.Contains(array, x.Member)</c>, the shape a <c>List&lt;T&gt;</c> (or an older compiler) gives.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class PredicateNormalizer
{
    private static readonly MethodInfo EnumerableContains = typeof(Enumerable).GetMethods()
        .Single(method => method.Name == nameof(Enumerable.Contains) && method.GetParameters().Length == 2);

    /// <summary>The expression with every span-bound <c>Contains</c> rewritten (the same instance when there is none).</summary>
    /// <param name="expression">An expression: a predicate, a projection, or a whole query.</param>
    public static TExpression Normalize<TExpression>(TExpression expression) where TExpression : Expression =>
        (TExpression)new Rewriter().Visit(expression);

    private sealed class Rewriter : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var visited = (MethodCallExpression)base.VisitMethodCall(node);
            if (visited.Method.DeclaringType != typeof(MemoryExtensions) || visited.Method.Name != nameof(MemoryExtensions.Contains)
                || visited.Arguments.Count is < 2 or > 3
                || (visited.Arguments.Count == 3 && visited.Arguments[2] is not ConstantExpression { Value: null })
                || Source(visited.Arguments[0]) is not { } source)
            {
                return visited;
            }

            var element = visited.Arguments[1].Type;
            return Expression.Call(EnumerableContains.MakeGenericMethod(element), source, visited.Arguments[1]);
        }

        /// <summary>The array (or other sequence) a span argument was converted from, or <c>null</c>.</summary>
        private static Expression? Source(Expression span)
        {
            var converted = span switch
            {
                MethodCallExpression { Method.Name: "op_Implicit" or "AsSpan", Arguments: [var argument] } => argument,
                UnaryExpression { NodeType: ExpressionType.Convert, Method.Name: "op_Implicit" } conversion => conversion.Operand,
                _ => null,
            };

            return converted is not null && converted.Type != span.Type
                                         && converted.Type.GetInterfaces().Any(type => type.IsGenericType
                                             && type.GetGenericTypeDefinition() == typeof(System.Collections.Generic.IEnumerable<>))
                ? converted
                : null;
        }
    }
}
