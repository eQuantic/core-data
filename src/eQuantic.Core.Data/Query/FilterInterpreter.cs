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
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
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

        if (TryNormalizeCompareTo(binary, out var normalized))
        {
            return normalized;
        }

        return Compare(binary, Comparison(binary.NodeType));
    }

    /// <summary>
    ///     Normalizes <c>member.CompareTo(value) &lt;op&gt; 0</c> into the direct comparison — the shape ordered
    ///     comparisons take for types without comparison operators (string, Guid), e.g. keyset paging predicates.
    /// </summary>
    private static bool TryNormalizeCompareTo(BinaryNode binary, out QueryFilter filter)
    {
        filter = null!;
        if (!TryComparison(binary.NodeType, out var op))
        {
            return false;
        }

        if (IsZero(binary.Right) && CompareToCall(binary.Left, out var member, out var value))
        {
            filter = new ComparisonFilter(member, op, value);
            return true;
        }

        if (IsZero(binary.Left) && CompareToCall(binary.Right, out member, out value))
        {
            filter = new ComparisonFilter(member, Flip(op), value);
            return true;
        }

        return false;
    }

    private static bool IsZero(ExpressionNode node) =>
        Unwrap(node) is ConstantNode { Value: 0 };

    private static bool CompareToCall(ExpressionNode node, out string member, out object? value)
    {
        member = string.Empty;
        value = null;
        if (Unwrap(node) is not MethodCallNode { Method.Name: nameof(IComparable.CompareTo), Object: { } receiver, Arguments: [var argument] })
        {
            return false;
        }

        if (Path(receiver) is not { } path || !NodeEvaluation.TryValue(argument, out value))
        {
            return false;
        }

        member = path;
        return true;
    }

    private static QueryFilter Compare(BinaryNode binary, ComparisonOperator op)
    {
        // x.Name.ToLower() == v (or ToUpper, either side): the member's folded text against the value as it is.
        if (op is ComparisonOperator.Equal or ComparisonOperator.NotEqual
            && (CaseFolded(binary.Left, binary.Right, out var folded) || CaseFolded(binary.Right, binary.Left, out folded)))
        {
            return op == ComparisonOperator.Equal ? folded : NotText(folded);
        }

        if (Path(binary.Left) is { } leftMember)
        {
            return new ComparisonFilter(leftMember, op, Value(binary.Right));
        }

        if (Path(binary.Right) is { } rightMember)
        {
            return new ComparisonFilter(rightMember, Flip(op), Value(binary.Left));
        }

        if (TryFunction(binary.Left, out var function, out var member, out var arguments))
        {
            return new FunctionFilter(function, member, arguments, op, Value(binary.Right));
        }

        if (TryFunction(binary.Right, out function, out member, out arguments))
        {
            return new FunctionFilter(function, member, arguments, Flip(op), Value(binary.Left));
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
            // C# lifts an ordered comparison of a nullable member, and a lifted comparison is false for null: its
            // negation holds for null, which the reversed comparison alone would not say (null <= v is not true either).
            var negated = Compare(binary, Negated(op));
            return op is not (ComparisonOperator.Equal or ComparisonOperator.NotEqual) && Lifted(binary)
                                                                                     && negated is ComparisonFilter { Value: not null } comparison
                ? new LogicalFilter(LogicalOperator.Or, [negated, new ComparisonFilter(comparison.Member, ComparisonOperator.Equal, null)])
                : negated;
        }

        var visited = Visit(operand);
        return visited is TextFilter ? NotText(visited) : new LogicalFilter(LogicalOperator.Not, [visited]);
    }

    /// <summary>
    ///     The negation of a text test: a member that is null fails <c>string.Equals(x.Name, v, comparison)</c> (C# answers
    ///     false), so it passes the negation, as it passes <c>x.Name != v</c>; the store's NOT alone would drop it.
    /// </summary>
    private static QueryFilter NotText(QueryFilter text) =>
        text is TextFilter { Match: TextMatch.Equals } equality
            ? new LogicalFilter(LogicalOperator.Or,
                [new LogicalFilter(LogicalOperator.Not, [text]), new ComparisonFilter(equality.Member, ComparisonOperator.Equal, null)])
            : new LogicalFilter(LogicalOperator.Not, [text]);

    /// <summary>Whether a comparison is lifted: the compiler typed one of its sides nullable.</summary>
    private static bool Lifted(BinaryNode binary) => Nullable(binary.Left) || Nullable(binary.Right);

    private static bool Nullable(ExpressionNode node) => node switch
    {
        UnaryNode { Type.Name: { } name } => name.StartsWith("System.Nullable", StringComparison.Ordinal),
        ConstantNode { Type.Name: { } name } => name.StartsWith("System.Nullable", StringComparison.Ordinal),
        _ => false,
    };

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
        if (TryText(call, out var textFilter))
        {
            return textFilter;
        }

        // string.StartsWith / EndsWith / Contains over a member — the single-argument overloads only, so the
        // store's collation decides case sensitivity exactly as string equality already does.
        if (call.Method.Name is nameof(string.StartsWith) or nameof(string.EndsWith)
            && call.Object is { } receiver && Path(receiver) is { } stringMember && call.Arguments is [var soleArgument]
            && Value(soleArgument) is string affix)
        {
            return new StringFilter(stringMember,
                call.Method.Name == nameof(string.StartsWith) ? StringOperator.StartsWith : StringOperator.EndsWith, affix);
        }

        if (call.Method.Name == "Contains" && IsStringMethod(call.Method)
            && call.Object is { } text && Path(text) is { } textMember && call.Arguments is [var fragmentArgument]
            && Value(fragmentArgument) is string fragment)
        {
            return new StringFilter(textMember, StringOperator.Contains, fragment);
        }

        if (call.Method.Name == "Contains" && !IsStringMethod(call.Method))
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

        // Any other call over a member — instance (member.Fn(args)) or marker-style static (Fn(member, args)) —
        // becomes a named function: the dialect's registry decides whether it translates; unknown names degrade
        // to the gated residual, where the method's real body runs.
        if (TryFunction(call, out var function, out var functionMember, out var arguments))
        {
            return new FunctionFilter(function, functionMember, arguments, op: null, value: null);
        }

        throw Unsupported(call);
    }

    // ---------------------------------------------------------------- text without regard to case

    /// <summary>
    ///     Recognizes a text test that does not follow the store's collation: <c>string.Equals(x.Name, v, comparison)</c>,
    ///     <c>x.Name.Equals(v, comparison)</c>, <c>x.Name.Contains/StartsWith/EndsWith(v, comparison)</c> with a comparison
    ///     that ignores case, and <c>Equals</c>/<c>Contains</c>/<c>StartsWith</c>/<c>EndsWith</c> over <c>x.Name.ToLower()</c>
    ///     or <c>ToUpper()</c>.
    /// </summary>
    private static bool TryText(MethodCallNode call, out QueryFilter filter)
    {
        filter = null!;
        var arguments = call.Arguments ?? [];
        var match = call.Method.Name switch
        {
            nameof(string.Equals) => TextMatch.Equals,
            nameof(string.StartsWith) => TextMatch.StartsWith,
            nameof(string.EndsWith) => TextMatch.EndsWith,
            nameof(string.Contains) => TextMatch.Contains,
            _ => (TextMatch?)null,
        };
        if (match is null || !IsStringMethod(call.Method))
        {
            return false;
        }

        // string.Equals(a, b, comparison): the member on either side.
        if (call.Object is null && match == TextMatch.Equals && arguments.Count == 3 && IgnoresCase(arguments[2]))
        {
            return Text(arguments[0], arguments[1], TextMatch.Equals, out filter) || Text(arguments[1], arguments[0], TextMatch.Equals, out filter);
        }

        if (call.Object is not { } receiver)
        {
            return false;
        }

        // x.Name.Op(v, comparison) — and v.Equals(x.Name, comparison).
        if (arguments.Count == 2 && IgnoresCase(arguments[1]))
        {
            return Text(receiver, arguments[0], match.Value, out filter)
                   || (match == TextMatch.Equals && Text(arguments[0], receiver, match.Value, out filter));
        }

        // x.Name.ToLower().Op(v)
        if (arguments.Count == 1 && Folding(receiver) is { } folding && NodeEvaluation.TryValue(arguments[0], out var value) && value is string text)
        {
            filter = new TextFilter(folding.Member, match.Value, text, folding.Case);
            return true;
        }

        return false;
    }

    /// <summary>A case-ignoring test of <paramref name="memberNode" /> against <paramref name="valueNode" />, when they are a member and a value.</summary>
    private static bool Text(ExpressionNode memberNode, ExpressionNode valueNode, TextMatch match, out QueryFilter filter)
    {
        filter = null!;
        if (Path(memberNode) is not { } member || !NodeEvaluation.TryValue(valueNode, out var value))
        {
            return false;
        }

        filter = value switch
        {
            string text => new TextFilter(member, match, text, TextCase.Ignore),
            null when match == TextMatch.Equals => new ComparisonFilter(member, ComparisonOperator.Equal, null),
            _ => throw new NotSupportedException($"A text match of '{member}' needs a string value."),
        };
        return true;
    }

    /// <summary><c>x.Name.ToLower() == v</c>: the folded member on one side, a string value on the other.</summary>
    private static bool CaseFolded(ExpressionNode foldedNode, ExpressionNode valueNode, out QueryFilter filter)
    {
        filter = null!;
        if (Folding(foldedNode) is not { } folding || !NodeEvaluation.TryValue(valueNode, out var value) || value is not string text)
        {
            return false;
        }

        filter = new TextFilter(folding.Member, TextMatch.Equals, text, folding.Case);
        return true;
    }

    /// <summary>The member and the case of <c>x.Name.ToLower()</c> / <c>ToLowerInvariant()</c> / <c>ToUpper()</c> / <c>ToUpperInvariant()</c>.</summary>
    private static (string Member, TextCase Case)? Folding(ExpressionNode node)
    {
        if (Unwrap(node) is not MethodCallNode { Object: { } receiver } call || call.Arguments is { Count: > 0 } || !IsStringMethod(call.Method)
            || Path(receiver) is not { } member)
        {
            return null;
        }

        return call.Method.Name switch
        {
            nameof(string.ToLower) or nameof(string.ToLowerInvariant) => (member, TextCase.Lower),
            nameof(string.ToUpper) or nameof(string.ToUpperInvariant) => (member, TextCase.Upper),
            _ => null,
        };
    }

    /// <summary>Whether the node is a <see cref="StringComparison" /> that ignores case (ordinal or invariant).</summary>
    private static bool IgnoresCase(ExpressionNode node) =>
        NodeEvaluation.TryValue(node, out var value) && value is StringComparison or int
                                                      && (StringComparison)Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)
                                                      is StringComparison.OrdinalIgnoreCase or StringComparison.InvariantCultureIgnoreCase;

    /// <summary>
    ///     Recognizes a function over a member: an instance call whose receiver is a member chain, or a static
    ///     (marker) call whose first argument is one — every remaining argument must fold to a constant.
    /// </summary>
    private static bool TryFunction(ExpressionNode node, out string function, out string member, out IReadOnlyList<object?> arguments)
    {
        function = string.Empty;
        member = string.Empty;
        arguments = [];

        if (Unwrap(node) is not MethodCallNode call)
        {
            return false;
        }

        if (call.Object is { } receiver && Path(receiver) is { } instanceMember)
        {
            return Collect(call, instanceMember, 0, ref function, ref member, ref arguments);
        }

        if (call.Object is null && call.Arguments is { Count: >= 1 } staticArguments && Path(staticArguments[0]) is { } markerMember)
        {
            return Collect(call, markerMember, 1, ref function, ref member, ref arguments);
        }

        return false;

        static bool Collect(MethodCallNode call, string memberPath, int skip,
            ref string function, ref string member, ref IReadOnlyList<object?> arguments)
        {
            var values = new List<object?>();
            var callArguments = call.Arguments ?? [];
            for (var index = skip; index < callArguments.Count; index++)
            {
                if (!NodeEvaluation.TryValue(callArguments[index], out var value))
                {
                    return false;
                }

                values.Add(value);
            }

            function = call.Method.Name;
            member = memberPath;
            arguments = values;
            return true;
        }
    }

    /// <summary>Whether the method is declared by <see cref="string" /> (disambiguates the two <c>Contains</c> shapes; the type reference may carry the alias or the full name).</summary>
    private static bool IsStringMethod(eQuantic.Linq.Expressions.Metadata.MethodRef method) =>
        method.DeclaringType?.Name is { } name
        && (name.Equals("string", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".String", StringComparison.Ordinal));

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
