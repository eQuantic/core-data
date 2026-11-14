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

    /// <summary>
    ///     Compiles a residual conjunct (one the store could not express) for client-side evaluation, with the member texts it
    ///     tests read as the stores read them: a member that is null fails a text test (<c>x.Name.Equals(v, comparison)</c>,
    ///     <c>StartsWith</c>, <c>EndsWith</c>, <c>Contains</c>) and stays null when folded (<c>x.Name.ToLower()</c>), instead of
    ///     throwing. So <c>x.Name.ToLower() != v</c> keeps the rows whose member is null on a store that evaluates it in memory,
    ///     as <c>x.Name != v</c> does and as the stores that push it down do.
    /// </summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="predicate">The residual conjunct.</param>
    public static Func<T, bool> CompileResidual<T>(LambdaExpression predicate) =>
        ((Expression<Func<T, bool>>)new NullSafeText().Visit(predicate)).Compile();

    private sealed class NullSafeText : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var visited = (MethodCallExpression)base.VisitMethodCall(node);
            if (visited.Method.DeclaringType != typeof(string) || visited.Object is not { } receiver || !ReadsMember(receiver))
            {
                return visited;
            }

            var isNull = Expression.Equal(receiver, Expression.Constant(null, typeof(string)));
            return visited.Method.Name switch
            {
                nameof(string.ToLower) or nameof(string.ToLowerInvariant) or nameof(string.ToUpper) or nameof(string.ToUpperInvariant)
                    when visited.Type == typeof(string) => Expression.Condition(isNull, Expression.Constant(null, typeof(string)), visited),
                nameof(string.Equals) or nameof(string.StartsWith) or nameof(string.EndsWith) or nameof(string.Contains)
                    when visited.Type == typeof(bool) => Expression.AndAlso(Expression.Not(isNull), visited),
                _ => visited,
            };
        }

        /// <summary>Whether the receiver is a member read off a parameter (or a text folded from one), which a row may hold null.</summary>
        private static bool ReadsMember(Expression receiver) =>
            receiver is MemberExpression { Expression: not null } member && Rooted(member.Expression)
            || receiver is ConditionalExpression { IfFalse: MethodCallExpression folded } && folded.Object is { } inner && ReadsMember(inner);

        private static bool Rooted(Expression expression) => expression switch
        {
            ParameterExpression => true,
            MemberExpression { Expression: { } inner } => Rooted(inner),
            UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.TypeAs } conversion => Rooted(conversion.Operand),
            _ => false,
        };
    }

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
