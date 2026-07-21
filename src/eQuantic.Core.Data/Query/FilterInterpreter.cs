using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using eQuantic.Linq.Expressions;
using eQuantic.Linq.Expressions.Nodes;

namespace eQuantic.Core.Data.Query;

/// <summary>
///     Interprets a typed predicate into the dialect-agnostic <see cref="QueryFilter" /> model. It first transforms
///     the predicate into eQuantic.Linq.Expressions' structured node model (<see cref="ExpressionSerializer.ToNode" />),
///     which partial-evaluates captured variables and computed sub-trees into constants and normalizes the tree — so
///     the visitor only ever meets columns and folded values, never closures or compiler plumbing. It then walks
///     those nodes: a parameter-rooted member access is a column, every other operand is a constant. It covers the
///     shapes real stores can express: comparisons; <c>IN</c> (from <c>Contains</c> and from OR-ed equalities on one
///     member); collection <c>CONTAINS</c>/<c>CONTAINS KEY</c>; boolean members (<c>x =&gt; x.Flag</c>); and
///     negation, pushed into the comparison where possible. Providers render the result to their query dialect.
/// </summary>
public static class FilterInterpreter
{
    /// <summary>Interprets a predicate into the filter model.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="predicate">The predicate.</param>
    public static QueryFilter Interpret<TEntity>(Expression<Func<TEntity, bool>> predicate) => Interpret((LambdaExpression)predicate);

    /// <summary>Interprets a predicate lambda into the filter model (untyped overload, for reflected predicates).</summary>
    /// <param name="predicate">The predicate lambda (its first parameter is the entity).</param>
    public static QueryFilter Interpret(LambdaExpression predicate)
    {
        // Transform to the structured node model: closures/captured variables and any parameter-free sub-tree are
        // folded to constants, so the visitor below deals only with parameter-rooted members and literal values.
        var lambda = ToNode(predicate);
        return Visit(lambda.Body);
    }

    /// <summary>
    ///     Converts a predicate into its node-model form — one partial-evaluation pass. Engines that split a
    ///     predicate (pushdown conjuncts, OR branches) convert once and interpret each part with
    ///     <see cref="Interpret(ExpressionNode)" />.
    /// </summary>
    /// <param name="predicate">The predicate lambda.</param>
    public static LambdaNode ToNode(LambdaExpression predicate) => (LambdaNode)NodeEvaluation.Serializer.ToNode(predicate);

    /// <summary>Interprets a node-model predicate body (or one of its conjuncts/branches) into the filter model.</summary>
    /// <param name="body">The predicate body node.</param>
    public static QueryFilter Interpret(ExpressionNode body) => Visit(body);

    /// <summary>
    ///     Rebuilds a typed predicate for a body node of a converted lambda — e.g. a conjunct the dialect refused,
    ///     handed back as a compilable expression for client-side residual evaluation.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="lambda">The converted lambda the node came from (its parameters anchor the rebuild).</param>
    /// <param name="body">The body node to rebuild.</param>
    public static Expression<Func<TEntity, bool>> RebuildPredicate<TEntity>(LambdaNode lambda, ExpressionNode body) =>
        NodeEvaluation.Serializer.ToExpression<Func<TEntity, bool>>(new LambdaNode { Parameters = lambda.Parameters, Body = body });

    private static QueryFilter Visit(ExpressionNode node)
    {
        node = Unwrap(node);
        return node switch
        {
            BinaryNode binary => Binary(binary),
            UnaryNode { NodeType: ExpressionType.Not, Operand: { } operand } => Negate(operand),
            MethodCallNode call => Call(call),
            MemberNode member when Path(member) is { } column => new ComparisonFilter(column, ComparisonOperator.Equal, true),
            _ => throw Unsupported(node),
        };
    }

    private static QueryFilter Binary(BinaryNode binary)
    {
        switch (binary.NodeType)
        {
            case ExpressionType.AndAlso or ExpressionType.And:
                return new LogicalFilter(LogicalOperator.And, [Visit(binary.Left), Visit(binary.Right)]);
            case ExpressionType.OrElse or ExpressionType.Or:
                return Or(Visit(binary.Left), Visit(binary.Right));
        }

        return Compare(binary, Comparison(binary.NodeType));
    }

    private static QueryFilter Compare(BinaryNode binary, ComparisonOperator op)
    {
        if (Path(binary.Left) is { } leftMember)
        {
            return new ComparisonFilter(leftMember, op, Value(binary.Right));
        }

        if (Path(binary.Right) is { } rightMember)
        {
            return new ComparisonFilter(rightMember, Flip(op), Value(binary.Left));
        }

        throw new NotSupportedException("Each filter clause must compare a member to a value.");
    }

    private static QueryFilter Negate(ExpressionNode operand)
    {
        operand = Unwrap(operand);

        if (operand is MemberNode member && Path(member) is { } column)
        {
            return new ComparisonFilter(column, ComparisonOperator.Equal, false);
        }

        if (operand is BinaryNode binary && TryComparison(binary.NodeType, out var op))
        {
            return Compare(binary, Negated(op));
        }

        return new LogicalFilter(LogicalOperator.Not, [Visit(operand)]);
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

    private static QueryFilter Call(MethodCallNode call)
    {
        if (call.Method.Name == "Contains")
        {
            // Gather the receiver and arguments; the parameter-rooted one is the column, the other is the collection.
            var operands = new List<ExpressionNode>();
            if (call.Object is not null)
            {
                operands.Add(call.Object);
            }

            if (call.Arguments is not null)
            {
                operands.AddRange(call.Arguments);
            }

            var member = operands.FirstOrDefault(operand => Path(operand) is not null);
            if (member is not null)
            {
                var column = Path(member)!;
                var other = operands.Single(operand => !ReferenceEquals(operand, member));

                // member.Contains(value): the collection is a column -> CONTAINS. Otherwise
                // source.Contains(member) / Enumerable.Contains(source, member) / MemoryExtensions.Contains(span, member) -> IN.
                return ReferenceEquals(call.Object, member)
                    ? new CollectionFilter(column, Value(other), key: false)
                    : new InFilter(column, Values(other));
            }
        }

        if (call.Method.Name == "ContainsKey" && call.Object is { } map && Path(map) is { } mapMember && call.Arguments is [var keyArgument])
        {
            return new CollectionFilter(mapMember, Value(keyArgument), key: true);
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

    /// <summary>The dotted member path when the node is a member chain rooted at the lambda parameter; else null.</summary>
    private static string? Path(ExpressionNode node)
    {
        var parts = new List<string>();
        var current = Unwrap(node);
        while (current is MemberNode member)
        {
            parts.Add(member.Member.Name);
            current = member.Expression is null ? null : Unwrap(member.Expression);
        }

        if (current is not ParameterNode || parts.Count == 0)
        {
            return null;
        }

        parts.Reverse();
        return string.Join(".", parts);
    }

    /// <summary>
    ///     The value of a constant operand — folded, or structural-but-parameter-free and evaluated by
    ///     <see cref="NodeEvaluation" /> (the same folding LINQ providers apply at translation time).
    /// </summary>
    private static object? Value(ExpressionNode node) =>
        NodeEvaluation.TryValue(node, out var value)
            ? value
            : throw new NotSupportedException("Each filter clause must compare a member to a constant value.");

    /// <summary>The elements of a constant collection (folded, inline-structural, or evaluated parameter-free).</summary>
    private static IReadOnlyList<object?> Values(ExpressionNode node) =>
        NodeEvaluation.TryValues(node, out var values)
            ? values
            : throw new NotSupportedException("An IN clause requires a constant collection of values.");

    /// <summary>Strips compiler-inserted conversions so a member/constant underneath is reached.</summary>
    private static ExpressionNode Unwrap(ExpressionNode node) => NodeEvaluation.Unwrap(node);

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

    private static NotSupportedException Unsupported(ExpressionNode node) =>
        new($"The filter shape '{node.GetType().Name}' is not supported.");
}
