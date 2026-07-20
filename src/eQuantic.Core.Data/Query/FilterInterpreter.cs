using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace eQuantic.Core.Data.Query;

/// <summary>
///     Interprets a typed predicate into the dialect-agnostic <see cref="QueryFilter" /> model. It walks the
///     expression tree, treats any parameter-rooted member access as a column and evaluates every other
///     (parameter-free) sub-tree to a value, so captured variables and computed constants just work. It covers the
///     shapes real stores can express: comparisons; <c>IN</c> (from <c>Contains</c> and from OR-ed equalities on
///     one member); collection <c>CONTAINS</c>/<c>CONTAINS KEY</c>; boolean members (<c>x =&gt; x.Flag</c>); and
///     negation, pushed into the comparison where possible. Providers render the result to their query dialect.
/// </summary>
public static class FilterInterpreter
{
    /// <summary>Interprets a predicate into the filter model.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="predicate">The predicate.</param>
    public static QueryFilter Interpret<TEntity>(Expression<Func<TEntity, bool>> predicate) =>
        Visit(predicate.Body, predicate.Parameters[0]);

    private static QueryFilter Visit(Expression expression, ParameterExpression parameter)
    {
        expression = Unwrap(expression);
        return expression switch
        {
            BinaryExpression binary => Binary(binary, parameter),
            UnaryExpression { NodeType: ExpressionType.Not, Operand: var operand } => Negate(operand, parameter),
            MethodCallExpression call => Call(call, parameter),
            MemberExpression member when Path(member, parameter) is { } column => new ComparisonFilter(column, ComparisonOperator.Equal, true),
            _ => throw Unsupported(expression),
        };
    }

    private static QueryFilter Binary(BinaryExpression binary, ParameterExpression parameter)
    {
        switch (binary.NodeType)
        {
            case ExpressionType.AndAlso or ExpressionType.And:
                return new LogicalFilter(LogicalOperator.And, [Visit(binary.Left, parameter), Visit(binary.Right, parameter)]);
            case ExpressionType.OrElse or ExpressionType.Or:
                return Or(Visit(binary.Left, parameter), Visit(binary.Right, parameter));
        }

        return Compare(binary, parameter, Comparison(binary.NodeType));
    }

    private static QueryFilter Compare(BinaryExpression binary, ParameterExpression parameter, ComparisonOperator op)
    {
        var left = Unwrap(binary.Left);
        var right = Unwrap(binary.Right);

        if (Path(left, parameter) is { } leftMember)
        {
            return new ComparisonFilter(leftMember, op, Evaluate(right));
        }

        if (Path(right, parameter) is { } rightMember)
        {
            return new ComparisonFilter(rightMember, Flip(op), Evaluate(left));
        }

        throw new NotSupportedException("Each filter clause must compare a member to a value.");
    }

    private static QueryFilter Negate(Expression operand, ParameterExpression parameter)
    {
        operand = Unwrap(operand);

        if (operand is MemberExpression member && Path(member, parameter) is { } column)
        {
            return new ComparisonFilter(column, ComparisonOperator.Equal, false);
        }

        if (operand is BinaryExpression binary && TryComparison(binary.NodeType, out var op))
        {
            return Compare(binary, parameter, Negated(op));
        }

        return new LogicalFilter(LogicalOperator.Not, [Visit(operand, parameter)]);
    }

    private static QueryFilter Or(QueryFilter left, QueryFilter right)
    {
        // OR of equalities/INs on one member folds into a single IN (the shape key-value stores express as IN).
        if (TryValues(left, out var leftMember, out var leftValues)
            && TryValues(right, out var rightMember, out var rightValues)
            && leftMember == rightMember)
        {
            return new InFilter(leftMember, [.. leftValues, .. rightValues]);
        }

        return new LogicalFilter(LogicalOperator.Or, [left, right]);
    }

    private static QueryFilter Call(MethodCallExpression call, ParameterExpression parameter)
    {
        if (call.Method.Name == "Contains")
        {
            // member.Contains(value): the collection is a column -> CONTAINS.
            if (call.Object is { } instance && Path(Unwrap(instance), parameter) is { } collectionMember && call.Arguments.Count == 1)
            {
                return new CollectionFilter(collectionMember, Evaluate(call.Arguments[0]), key: false);
            }

            // source.Contains(member) / Enumerable.Contains(source, member) / MemoryExtensions.Contains(span, member) -> IN.
            var (collection, value) = call.Object is { } source
                ? (source, call.Arguments[0])
                : (call.Arguments[0], call.Arguments[1]);
            if (Path(Unwrap(value), parameter) is { } inMember)
            {
                return new InFilter(inMember, Values(Evaluate(Unwrap(collection))));
            }
        }

        if (call.Method.Name == "ContainsKey" && call.Object is { } map && Path(Unwrap(map), parameter) is { } mapMember && call.Arguments.Count == 1)
        {
            return new CollectionFilter(mapMember, Evaluate(call.Arguments[0]), key: true);
        }

        throw Unsupported(call);
    }

    // ---------------------------------------------------------------- helpers

    private static bool TryValues(QueryFilter filter, out string member, out IReadOnlyList<object?> values)
    {
        switch (filter)
        {
            case ComparisonFilter { Operator: ComparisonOperator.Equal } comparison:
                member = comparison.Member;
                values = [comparison.Value];
                return true;
            case InFilter inFilter:
                member = inFilter.Member;
                values = inFilter.Values;
                return true;
            default:
                member = string.Empty;
                values = [];
                return false;
        }
    }

    /// <summary>The dotted member path when the expression is a member chain rooted at the lambda parameter; else null.</summary>
    private static string? Path(Expression expression, ParameterExpression parameter)
    {
        var parts = new List<string>();
        var current = Unwrap(expression);
        while (current is MemberExpression member)
        {
            parts.Add(member.Member.Name);
            current = member.Expression is null ? null! : Unwrap(member.Expression);
        }

        if (current != parameter || parts.Count == 0)
        {
            return null;
        }

        parts.Reverse();
        return string.Join(".", parts);
    }

    private static object? Evaluate(Expression expression)
    {
        expression = Unwrap(expression);
        if (expression is ConstantExpression constant)
        {
            return constant.Value;
        }

        return Expression.Lambda(expression).Compile().DynamicInvoke();
    }

    private static IReadOnlyList<object?> Values(object? source) =>
        source is IEnumerable enumerable
            ? enumerable.Cast<object?>().ToList()
            : throw new NotSupportedException("An IN clause requires a constant collection of values.");

    private static Expression Unwrap(Expression expression) => expression switch
    {
        // Compiler-inserted conversions: casts (Convert) and conversion operators such as the array →
        // ReadOnlySpan op_Implicit that binds `array.Contains(x)` to MemoryExtensions.Contains.
        UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked, Operand: var operand } => Unwrap(operand),
        MethodCallExpression { Object: null, Method.Name: "op_Implicit" or "op_Explicit", Arguments: [var argument] } => Unwrap(argument),
        _ => expression,
    };

    private static ComparisonOperator Comparison(ExpressionType nodeType) =>
        TryComparison(nodeType, out var op) ? op : throw new NotSupportedException($"The operator '{nodeType}' is not a supported comparison.");

    private static bool TryComparison(ExpressionType nodeType, out ComparisonOperator op)
    {
        op = nodeType switch
        {
            ExpressionType.Equal => ComparisonOperator.Equal,
            ExpressionType.NotEqual => ComparisonOperator.NotEqual,
            ExpressionType.GreaterThan => ComparisonOperator.GreaterThan,
            ExpressionType.GreaterThanOrEqual => ComparisonOperator.GreaterThanOrEqual,
            ExpressionType.LessThan => ComparisonOperator.LessThan,
            ExpressionType.LessThanOrEqual => ComparisonOperator.LessThanOrEqual,
            _ => (ComparisonOperator)(-1),
        };
        return (int)op >= 0;
    }

    private static ComparisonOperator Flip(ComparisonOperator op) => op switch
    {
        ComparisonOperator.GreaterThan => ComparisonOperator.LessThan,
        ComparisonOperator.GreaterThanOrEqual => ComparisonOperator.LessThanOrEqual,
        ComparisonOperator.LessThan => ComparisonOperator.GreaterThan,
        ComparisonOperator.LessThanOrEqual => ComparisonOperator.GreaterThanOrEqual,
        _ => op,
    };

    private static ComparisonOperator Negated(ComparisonOperator op) => op switch
    {
        ComparisonOperator.Equal => ComparisonOperator.NotEqual,
        ComparisonOperator.NotEqual => ComparisonOperator.Equal,
        ComparisonOperator.GreaterThan => ComparisonOperator.LessThanOrEqual,
        ComparisonOperator.GreaterThanOrEqual => ComparisonOperator.LessThan,
        ComparisonOperator.LessThan => ComparisonOperator.GreaterThanOrEqual,
        ComparisonOperator.LessThanOrEqual => ComparisonOperator.GreaterThan,
        _ => op,
    };

    private static NotSupportedException Unsupported(Expression expression) =>
        new($"The filter shape '{expression.NodeType}' is not supported.");
}
