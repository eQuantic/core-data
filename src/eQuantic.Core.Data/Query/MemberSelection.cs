using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace eQuantic.Core.Data.Query;

/// <summary>
///     Reads the members a selector names — <c>x =&gt; x.Email</c>, or <c>x =&gt; new { x.WorkspaceId, x.Name }</c> for
///     several — the shape an upsert's target (<c>on</c>) and a member-scoped <c>Modify</c> take. Anything but direct
///     member reads off the parameter is refused, naming the shape that is accepted.
///     <b>Provider SPI</b>: application code never needs to call it.
/// </summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class MemberSelection
{
    /// <summary>The members the selector names, in the order it names them.</summary>
    /// <param name="selector">The selector.</param>
    /// <returns>The members, never empty.</returns>
    public static IReadOnlyList<MemberInfo> Members(LambdaExpression selector)
    {
        if (selector is null)
        {
            throw new ArgumentNullException(nameof(selector));
        }

        var parameter = selector.Parameters[0];
        var body = Unwrap(selector.Body);
        var members = new List<MemberInfo>();
        switch (body)
        {
            case NewExpression { Arguments.Count: > 0 } created:
                foreach (var argument in created.Arguments)
                {
                    members.Add(Read(Unwrap(argument), parameter, selector));
                }

                break;
            case MemberInitExpression { Bindings.Count: > 0 } initialized:
                foreach (var binding in initialized.Bindings)
                {
                    members.Add(binding is MemberAssignment assignment
                        ? Read(Unwrap(assignment.Expression), parameter, selector)
                        : throw Refusal(selector));
                }

                break;
            default:
                members.Add(Read(body, parameter, selector));
                break;
        }

        return members;
    }

    /// <summary>The names of the members the selector names, in the order it names them.</summary>
    /// <param name="selector">The selector.</param>
    public static IReadOnlyList<string> Names(LambdaExpression selector)
    {
        var members = Members(selector);
        var names = new List<string>(members.Count);
        foreach (var member in members)
        {
            names.Add(member.Name);
        }

        return names;
    }

    private static MemberInfo Read(Expression expression, ParameterExpression parameter, LambdaExpression selector) =>
        expression is MemberExpression { Expression: { } target } access && Unwrap(target) == parameter
            ? access.Member
            : throw Refusal(selector);

    private static Expression Unwrap(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            expression = convert.Operand;
        }

        return expression;
    }

    private static NotSupportedException Refusal(LambdaExpression selector) =>
        new($"'{selector}' does not name members: name one member (x => x.Email) or several in an anonymous type " +
            "(x => new { x.WorkspaceId, x.Name }), each read directly off the entity.");
}
