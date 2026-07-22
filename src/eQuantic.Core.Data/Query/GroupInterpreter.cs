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
                    return new GroupAggregateBinding(target, call.Method.Name switch
                    {
                        nameof(Enumerable.Sum) => GroupAggregate.Sum,
                        nameof(Enumerable.Min) => GroupAggregate.Min,
                        nameof(Enumerable.Max) => GroupAggregate.Max,
                        _ => GroupAggregate.Average,
                    }, member);
            }
        }

        throw Unsupported($"'{target}'");
    }

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
