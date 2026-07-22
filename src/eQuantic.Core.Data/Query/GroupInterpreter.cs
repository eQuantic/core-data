using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using eQuantic.Linq.Expressions.Nodes;

namespace eQuantic.Core.Data.Query;

/// <summary>A grouped aggregate in the dialect-agnostic model.</summary>
public enum GroupAggregate
{
    /// <summary><c>COUNT(*)</c>.</summary>
    Count,

    /// <summary><c>SUM(member)</c>.</summary>
    Sum,

    /// <summary><c>MIN(member)</c>.</summary>
    Min,

    /// <summary><c>MAX(member)</c>.</summary>
    Max,

    /// <summary><c>AVG(member)</c> (integer members cast before averaging).</summary>
    Average,
}

/// <summary>One member of the grouping key: its entity path and, for composite keys, its name on the key type.</summary>
/// <param name="Path">The entity member path.</param>
/// <param name="Name">The member's name on the composite key type, or <c>null</c> for a single-member key.</param>
public sealed record GroupKeyMember(string Path, string? Name);

/// <summary>One projected member of a grouped result.</summary>
public abstract class GroupBinding
{
    /// <summary>Initializes the binding.</summary>
    /// <param name="target">The projected member's name on the result type.</param>
    protected GroupBinding(string target) => Target = target;

    /// <summary>The projected member's name on the result type.</summary>
    public string Target { get; }
}

/// <summary>Projects the grouping key — whole (<c>g.Key</c>) or one member of a composite key (<c>g.Key.A</c>).</summary>
public sealed class GroupKeyBinding(string target, string? keyName) : GroupBinding(target)
{
    /// <summary>The composite key member's name, or <c>null</c> for the whole key.</summary>
    public string? KeyName { get; } = keyName;
}

/// <summary>Projects an aggregate over the group (<c>g.Count()</c>, <c>g.Sum(x =&gt; x.Member)</c>, …).</summary>
public sealed class GroupAggregateBinding(string target, GroupAggregate aggregate, string? member) : GroupBinding(target)
{
    /// <summary>The aggregate.</summary>
    public GroupAggregate Aggregate { get; } = aggregate;

    /// <summary>The aggregated member path, or <c>null</c> for <see cref="GroupAggregate.Count" />.</summary>
    public string? Member { get; } = member;
}

/// <summary>The interpreted grouped query: the key members and the projected bindings, in projection order.</summary>
/// <param name="Key">The grouping key members, in order.</param>
/// <param name="Bindings">The projected bindings, in order.</param>
/// <param name="ConstructorProjection">Whether the result is built positionally (anonymous/ctor) rather than by member init.</param>
public sealed record GroupQuery(IReadOnlyList<GroupKeyMember> Key, IReadOnlyList<GroupBinding> Bindings, bool ConstructorProjection);

/// <summary>A predicate over a group — the dialect-agnostic <c>HAVING</c> model.</summary>
public abstract class GroupPredicate
{
}

/// <summary>Compares an aggregate — or a key member — against a value.</summary>
public sealed class GroupComparison(GroupAggregate? aggregate, string? member, ComparisonOperator op, object? value) : GroupPredicate
{
    /// <summary>The aggregate, or <c>null</c> when the comparison is over a key member.</summary>
    public GroupAggregate? Aggregate { get; } = aggregate;

    /// <summary>The aggregated member path (<c>null</c> for <see cref="GroupAggregate.Count" />), or the key member's entity path.</summary>
    public string? Member { get; } = member;

    /// <summary>The comparison operator.</summary>
    public ComparisonOperator Operator { get; } = op;

    /// <summary>The compared value.</summary>
    public object? Value { get; } = value;
}

/// <summary>Combines group predicates with <c>AND</c>/<c>OR</c>.</summary>
public sealed class GroupLogical(LogicalOperator op, IReadOnlyList<GroupPredicate> operands) : GroupPredicate
{
    /// <summary>The logical operator.</summary>
    public LogicalOperator Operator { get; } = op;

    /// <summary>The combined operands.</summary>
    public IReadOnlyList<GroupPredicate> Operands { get; } = operands;
}

/// <summary>
///     Interprets a typed <c>GroupBy</c> — a key selector plus a result selector over the grouping — into the
///     dialect-agnostic <see cref="GroupQuery" />. Only the shapes a store can aggregate server-side are
///     accepted: the key is a member (or an anonymous composite of members), and each projected member is
///     <c>g.Key</c>, <c>g.Key.Member</c>, <c>g.Count()</c> or <c>g.Sum/Min/Max/Average(x =&gt; x.Member)</c>.
///     Anything else is rejected with the supported shapes — a grouped read never silently degrades to
///     fetching the table.
/// </summary>
public static class GroupInterpreter
{
    /// <summary>Interprets the selectors into the grouped-query model.</summary>
    public static GroupQuery Interpret<TEntity, TKey, TResult>(
        Expression<Func<TEntity, TKey>> keySelector,
        Expression<Func<IGrouping<TKey, TEntity>, TResult>> resultSelector)
    {
        var key = Key(FilterInterpreter.ToNode(keySelector).Body);
        var result = FilterInterpreter.ToNode(resultSelector);
        var grouping = result.Parameters[0];

        switch (NodeEvaluation.Unwrap(result.Body))
        {
            case NewNode { Members: { Count: > 0 } members, Arguments: { } arguments } when members.Count == arguments.Count:
            {
                var bindings = members
                    .Select((member, index) => Binding(member.Name, arguments[index], grouping, key))
                    .ToList();
                return new GroupQuery(key, bindings, ConstructorProjection: true);
            }

            case MemberInitNode memberInit:
            {
                var bindings = memberInit.Bindings
                    .Select(binding => binding is MemberAssignmentNode assignment
                        ? Binding(assignment.Member.Name, assignment.Expression, grouping, key)
                        : throw Unsupported($"the binding to '{binding.Member.Name}'"))
                    .ToList();
                return new GroupQuery(key, bindings, ConstructorProjection: false);
            }

            default:
                throw Unsupported("the result selector");
        }
    }

    private static IReadOnlyList<GroupKeyMember> Key(ExpressionNode body)
    {
        body = NodeEvaluation.Unwrap(body);

        if (MemberPath(body) is { } single)
        {
            return [new GroupKeyMember(single, null)];
        }

        if (body is NewNode { Members: { Count: > 0 } members, Arguments: { } arguments } && members.Count == arguments.Count)
        {
            return members
                .Select((member, index) => new GroupKeyMember(
                    MemberPath(arguments[index]) ?? throw Unsupported($"the key member '{member.Name}'"), member.Name))
                .ToList();
        }

        throw Unsupported("the key selector");
    }

    private static GroupBinding Binding(string target, ExpressionNode value, ParameterNode grouping, IReadOnlyList<GroupKeyMember> key)
    {
        value = NodeEvaluation.Unwrap(value);

        // g.Key — the whole key.
        if (value is MemberNode { Member.Name: "Key", Expression: { } keyOwner } && IsGrouping(keyOwner, grouping))
        {
            return new GroupKeyBinding(target, null);
        }

        // g.Key.Member — one member of a composite key.
        if (value is MemberNode { Expression: { } inner } outer
            && NodeEvaluation.Unwrap(inner) is MemberNode { Member.Name: "Key", Expression: { } owner }
            && IsGrouping(owner, grouping))
        {
            var name = outer.Member.Name;
            return key.Any(member => string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase))
                ? new GroupKeyBinding(target, name)
                : throw Unsupported($"'{target}': the key has no member '{name}'");
        }

        // g.Count() / g.Sum(x => x.Member) and friends.
        if (value is MethodCallNode { Object: null, Arguments: { Count: >= 1 } arguments } call
            && IsGrouping(arguments[0], grouping))
        {
            switch (call.Method.Name)
            {
                case nameof(Enumerable.Count) or nameof(Enumerable.LongCount) when arguments.Count == 1:
                    return new GroupAggregateBinding(target, GroupAggregate.Count, null);
                case nameof(Enumerable.Sum) or nameof(Enumerable.Min) or nameof(Enumerable.Max) or nameof(Enumerable.Average)
                    when arguments.Count == 2 && NodeEvaluation.Unwrap(arguments[1]) is LambdaNode selector
                         && MemberPath(selector.Body) is { } member:
                    return new GroupAggregateBinding(target, AggregateOf(call.Method.Name), member);
            }
        }

        throw Unsupported($"'{target}'");
    }

    /// <summary>
    ///     Interprets a <c>HAVING</c> predicate over the grouping into the dialect-agnostic model. Supported
    ///     shapes: comparisons (<c>==</c>, <c>!=</c>, <c>&gt;</c>, <c>&gt;=</c>, <c>&lt;</c>, <c>&lt;=</c>) of
    ///     <c>g.Count()</c>, <c>g.Sum/Min/Max/Average(x =&gt; x.Member)</c> or a key member (<c>g.Key</c>,
    ///     <c>g.Key.Member</c>) against values, combined with <c>&amp;&amp;</c>, <c>||</c> and <c>!</c>.
    /// </summary>
    /// <param name="having">The predicate over the grouping.</param>
    /// <param name="key">The interpreted grouping key (from <see cref="Interpret{TEntity,TKey,TResult}" />).</param>
    public static GroupPredicate InterpretHaving<TEntity, TKey>(
        Expression<Func<IGrouping<TKey, TEntity>, bool>> having, IReadOnlyList<GroupKeyMember> key)
    {
        var lambda = FilterInterpreter.ToNode(having);
        return Having(lambda.Body, lambda.Parameters[0], key, negated: false);
    }

    private static GroupPredicate Having(ExpressionNode node, ParameterNode grouping, IReadOnlyList<GroupKeyMember> key, bool negated)
    {
        node = NodeEvaluation.Unwrap(node);
        switch (node)
        {
            case BinaryNode { NodeType: ExpressionType.AndAlso or ExpressionType.And } logical:
                return new GroupLogical(negated ? LogicalOperator.Or : LogicalOperator.And,
                    [Having(logical.Left, grouping, key, negated), Having(logical.Right, grouping, key, negated)]);

            case BinaryNode { NodeType: ExpressionType.OrElse or ExpressionType.Or } logical:
                return new GroupLogical(negated ? LogicalOperator.And : LogicalOperator.Or,
                    [Having(logical.Left, grouping, key, negated), Having(logical.Right, grouping, key, negated)]);

            case UnaryNode { NodeType: ExpressionType.Not, Operand: { } operand }:
                return Having(operand, grouping, key, !negated);

            case BinaryNode binary when ComparisonOf(binary.NodeType) is { } op:
            {
                if (TryAtom(binary.Left, grouping, key, out var aggregate, out var member)
                    && NodeEvaluation.TryValue(binary.Right, out var value))
                {
                    return new GroupComparison(aggregate, member, Apply(op, negated), value);
                }

                if (TryAtom(binary.Right, grouping, key, out aggregate, out member)
                    && NodeEvaluation.TryValue(binary.Left, out value))
                {
                    return new GroupComparison(aggregate, member, Apply(Flip(op), negated), value);
                }

                throw UnsupportedHaving();
            }

            default:
                throw UnsupportedHaving();
        }
    }

    private static bool TryAtom(ExpressionNode node, ParameterNode grouping, IReadOnlyList<GroupKeyMember> key,
        out GroupAggregate? aggregate, out string? member)
    {
        aggregate = null;
        member = null;
        node = NodeEvaluation.Unwrap(node);

        // g.Count() / g.Sum(x => x.Member) and friends.
        if (node is MethodCallNode { Object: null, Arguments: { Count: >= 1 } arguments } call
            && IsGrouping(arguments[0], grouping))
        {
            switch (call.Method.Name)
            {
                case nameof(Enumerable.Count) or nameof(Enumerable.LongCount) when arguments.Count == 1:
                    aggregate = GroupAggregate.Count;
                    return true;
                case nameof(Enumerable.Sum) or nameof(Enumerable.Min) or nameof(Enumerable.Max) or nameof(Enumerable.Average)
                    when arguments.Count == 2 && NodeEvaluation.Unwrap(arguments[1]) is LambdaNode selector
                         && MemberPath(selector.Body) is { } path:
                    aggregate = AggregateOf(call.Method.Name);
                    member = path;
                    return true;
            }

            return false;
        }

        // g.Key — a single-member key compares whole; a composite key compares one member at a time.
        if (node is MemberNode { Member.Name: "Key", Expression: { } owner } && IsGrouping(owner, grouping))
        {
            if (key is not [{ Name: null } single])
            {
                return false;
            }

            member = single.Path;
            return true;
        }

        // g.Key.Member of a composite key → that member's entity path.
        if (node is MemberNode { Expression: { } inner } outer
            && NodeEvaluation.Unwrap(inner) is MemberNode { Member.Name: "Key", Expression: { } keyOwner }
            && IsGrouping(keyOwner, grouping))
        {
            member = key.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, outer.Member.Name, StringComparison.OrdinalIgnoreCase))?.Path;
            return member is not null;
        }

        return false;
    }

    private static ComparisonOperator? ComparisonOf(ExpressionType type) => type switch
    {
        ExpressionType.Equal => ComparisonOperator.Equal,
        ExpressionType.NotEqual => ComparisonOperator.NotEqual,
        ExpressionType.GreaterThan => ComparisonOperator.GreaterThan,
        ExpressionType.GreaterThanOrEqual => ComparisonOperator.GreaterThanOrEqual,
        ExpressionType.LessThan => ComparisonOperator.LessThan,
        ExpressionType.LessThanOrEqual => ComparisonOperator.LessThanOrEqual,
        _ => null,
    };

    private static ComparisonOperator Flip(ComparisonOperator op) => op switch
    {
        ComparisonOperator.GreaterThan => ComparisonOperator.LessThan,
        ComparisonOperator.GreaterThanOrEqual => ComparisonOperator.LessThanOrEqual,
        ComparisonOperator.LessThan => ComparisonOperator.GreaterThan,
        ComparisonOperator.LessThanOrEqual => ComparisonOperator.GreaterThanOrEqual,
        _ => op,
    };

    private static ComparisonOperator Apply(ComparisonOperator op, bool negated) => !negated ? op : op switch
    {
        ComparisonOperator.Equal => ComparisonOperator.NotEqual,
        ComparisonOperator.NotEqual => ComparisonOperator.Equal,
        ComparisonOperator.GreaterThan => ComparisonOperator.LessThanOrEqual,
        ComparisonOperator.GreaterThanOrEqual => ComparisonOperator.LessThan,
        ComparisonOperator.LessThan => ComparisonOperator.GreaterThanOrEqual,
        _ => ComparisonOperator.GreaterThan,
    };

    private static GroupAggregate AggregateOf(string method) => method switch
    {
        nameof(Enumerable.Sum) => GroupAggregate.Sum,
        nameof(Enumerable.Min) => GroupAggregate.Min,
        nameof(Enumerable.Max) => GroupAggregate.Max,
        _ => GroupAggregate.Average,
    };

    private static NotSupportedException UnsupportedHaving() => new(
        "Cannot interpret the HAVING predicate. Supported shapes: comparisons (==, !=, >, >=, <, <=) of g.Count(), " +
        "g.Sum/Min/Max/Average(x => x.Member) or a key member (g.Key, g.Key.Member) against values, combined with " +
        "&&, || and !.");

    private static bool IsGrouping(ExpressionNode node, ParameterNode grouping) =>
        NodeEvaluation.Unwrap(node) is ParameterNode parameter && parameter.Id == grouping.Id;

    private static string? MemberPath(ExpressionNode node)
    {
        var parts = new List<string>();
        var current = NodeEvaluation.Unwrap(node);
        while (current is MemberNode member)
        {
            parts.Add(member.Member.Name);
            current = member.Expression is null ? null : NodeEvaluation.Unwrap(member.Expression);
        }

        if (current is not ParameterNode || parts.Count == 0)
        {
            return null;
        }

        parts.Reverse();
        return string.Join(".", parts);
    }

    private static NotSupportedException Unsupported(string what) => new(
        $"Cannot group by {what}. Supported shapes: a member (or anonymous composite of members) as the key, and " +
        "g.Key, g.Key.Member, g.Count(), g.Sum/Min/Max/Average(x => x.Member) as the projected members.");
}
