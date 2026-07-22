using System;
using System.Collections.Generic;
using System.Linq;
using eQuantic.Linq.Expressions.Nodes;

namespace eQuantic.Core.Data.Query;

/// <summary>One projected member of a union branch.</summary>
public abstract class UnionBinding
{
    /// <summary>Initializes the binding.</summary>
    /// <param name="target">The projected member's name on the result type.</param>
    protected UnionBinding(string target) => Target = target;

    /// <summary>The projected member's name on the result type.</summary>
    public string Target { get; }
}

/// <summary>Projects an entity member.</summary>
public sealed class UnionColumnBinding(string target, string member) : UnionBinding(target)
{
    /// <summary>The entity member path.</summary>
    public string Member { get; } = member;
}

/// <summary>Projects a constant — typically a per-branch tag naming where a row came from.</summary>
public sealed class UnionConstantBinding(string target, object? value) : UnionBinding(target)
{
    /// <summary>The constant value.</summary>
    public object? Value { get; } = value;
}

/// <summary>The interpreted branch projection: the bindings in projection order.</summary>
/// <param name="Bindings">The projected bindings, in order.</param>
/// <param name="ConstructorProjection">Whether the result is built positionally (anonymous/ctor) rather than by member init.</param>
public sealed record UnionProjection(IReadOnlyList<UnionBinding> Bindings, bool ConstructorProjection);

/// <summary>
///     Interprets a union branch's projection over the node model into the dialect-agnostic
///     <see cref="UnionProjection" />. Each projected member must be an entity member or a constant — the shapes
///     every store can place in a combined select. Anything else is rejected with the supported shapes; a union
///     never silently degrades to fetching the tables.
/// </summary>
public static class UnionInterpreter
{
    /// <summary>
    ///     Interprets every branch's projection and validates the branches against each other: the first branch
    ///     defines the target order, later branches are reordered into it, and a branch that does not project the
    ///     same members is rejected — a union must combine one shape.
    /// </summary>
    /// <param name="branches">The composed branches, in order.</param>
    /// <returns>One aligned projection per branch, in branch order.</returns>
    public static IReadOnlyList<UnionProjection> InterpretAll(IReadOnlyList<UnionBranch> branches)
    {
        var projections = new List<UnionProjection>(branches.Count);
        IReadOnlyList<string> targets = [];

        for (var index = 0; index < branches.Count; index++)
        {
            var projection = Interpret(branches[index]);
            if (index == 0)
            {
                targets = projection.Bindings.Select(binding => binding.Target).ToList();
            }
            else
            {
                projection = Align(projection, targets, index);
            }

            projections.Add(projection);
        }

        return projections;
    }

    private static UnionProjection Align(UnionProjection projection, IReadOnlyList<string> targets, int index)
    {
        var byTarget = new Dictionary<string, UnionBinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in projection.Bindings)
        {
            if (!byTarget.TryAdd(binding.Target, binding))
            {
                throw Mismatch(index);
            }
        }

        if (byTarget.Count != targets.Count)
        {
            throw Mismatch(index);
        }

        var ordered = targets
            .Select(target => byTarget.TryGetValue(target, out var binding) ? binding : throw Mismatch(index))
            .ToList();
        return projection with { Bindings = ordered };
    }

    private static NotSupportedException Mismatch(int index) => new(
        $"Union branch {index + 1} does not project the same members as the first branch — every branch must " +
        "produce the same shape.");

    /// <summary>Interprets the branch's projection.</summary>
    /// <param name="branch">The composed branch.</param>
    public static UnionProjection Interpret(UnionBranch branch)
    {
        var lambda = FilterInterpreter.ToNode(branch.Projection);

        switch (NodeEvaluation.Unwrap(lambda.Body))
        {
            case NewNode { Members: { Count: > 0 } members, Arguments: { } arguments } when members.Count == arguments.Count:
                return new UnionProjection(
                    members.Select((member, index) => Binding(member.Name, arguments[index])).ToList(),
                    ConstructorProjection: true);

            case MemberInitNode memberInit:
                return new UnionProjection(
                    memberInit.Bindings
                        .Select(binding => binding is MemberAssignmentNode assignment
                            ? Binding(assignment.Member.Name, assignment.Expression)
                            : throw Unsupported($"the binding to '{binding.Member.Name}'"))
                        .ToList(),
                    ConstructorProjection: false);

            default:
                throw Unsupported("the projection");
        }
    }

    private static UnionBinding Binding(string target, ExpressionNode value)
    {
        if (MemberPath(value) is { } member)
        {
            return new UnionColumnBinding(target, member);
        }

        if (NodeEvaluation.TryValue(value, out var constant))
        {
            return new UnionConstantBinding(target, constant);
        }

        throw Unsupported($"'{target}'");
    }

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
        $"Cannot project {what} in a union branch. Each projected member must be an entity member or a constant " +
        "(a per-branch tag); compute derived values after materialization.");
}
