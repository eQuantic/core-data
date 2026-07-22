using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using eQuantic.Linq.Expressions.Nodes;

namespace eQuantic.Core.Data.Query;

/// <summary>
///     Interprets a member-initialisation update factory (<c>x =&gt; new TEntity { Status = "Closed", N = x.N + 1,
///     Tags = x.Tags.Append("vip").ToList() }</c>) into the dialect-agnostic <see cref="UpdateAssignment" /> model.
///     The factory is first transformed into eQuantic.Linq.Expressions' structured node model — captured variables
///     and parameter-free sub-trees fold to constants, compiler plumbing is normalized — and the analysis walks
///     those nodes (a <see cref="LambdaNode" /> can also be supplied directly, e.g. deserialized from the wire).
///     A constant assignment becomes a <see cref="SetAssignment" />; an assignment that reads the entity is
///     recognised when a store can apply it atomically — <c>member ± n</c> and <c>member * n</c> (numeric), and
///     <c>Append</c>/<c>Prepend</c>/<c>Concat</c>/<c>Union</c>/<c>Except</c> over the same collection member
///     (optionally wrapped in <c>ToList</c>/<c>ToArray</c>/<c>ToHashSet</c>). Anything else is rejected with the
///     supported shapes, so an update never silently degrades to a read-modify-write.
/// </summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class UpdateInterpreter
{
    /// <summary>Interprets the update factory into the assignment model.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="updateFactory">The member-initialisation update factory.</param>
    public static IReadOnlyList<UpdateAssignment> Interpret<TEntity>(Expression<Func<TEntity, TEntity>> updateFactory)
    {
        if (updateFactory is null)
        {
            throw new ArgumentNullException(nameof(updateFactory));
        }

        return Interpret<TEntity>((LambdaNode)NodeEvaluation.Serializer.ToNode(updateFactory));
    }

    /// <summary>Interprets a node-model update factory (e.g. received over the wire) into the assignment model.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="updateFactory">The update factory as a converted lambda.</param>
    public static IReadOnlyList<UpdateAssignment> Interpret<TEntity>(LambdaNode updateFactory)
    {
        if (updateFactory is null)
        {
            throw new ArgumentNullException(nameof(updateFactory));
        }

        if (updateFactory.Body is not MemberInitNode memberInit)
        {
            throw new NotSupportedException(
                "UpdateMany expects a member-initialisation update, e.g. x => new " + typeof(TEntity).Name + " { Status = \"Closed\" }.");
        }

        var assignments = new List<UpdateAssignment>();
        foreach (var binding in memberInit.Bindings)
        {
            if (binding is not MemberAssignmentNode assignment)
            {
                throw new NotSupportedException(
                    $"Only member assignments are supported in an UpdateMany update; got '{binding.GetType().Name}'.");
            }

            assignments.Add(Assignment(Member(typeof(TEntity), assignment), assignment.Expression));
        }

        if (assignments.Count == 0)
        {
            throw new NotSupportedException("The UpdateMany update assigns no fields.");
        }

        return assignments;
    }

    private static UpdateAssignment Assignment(MemberInfo member, ExpressionNode expression)
    {
        expression = NodeEvaluation.Unwrap(expression);

        // Captured/constant values arrive folded; check the computed shapes before the (compile-based)
        // parameter-free evaluation so the common read-modify-write path never pays a failed compile.
        if (expression is ConstantNode constant)
        {
            return new SetAssignment(member, constant.Value);
        }

        if (Computed(member, expression) is { } computed)
        {
            return computed;
        }

        if (NodeEvaluation.TryValue(expression, out var value))
        {
            return new SetAssignment(member, value);
        }

        throw new NotSupportedException(
            $"The assignment to '{member.Name}' references the entity in a shape no store can apply atomically. " +
            "Supported computed shapes: member + n, member - n, member * n (numeric members), and " +
            "Append/Prepend/Concat/Union/Except over the same collection member.");
    }

    private static UpdateAssignment? Computed(MemberInfo member, ExpressionNode expression)
    {
        switch (expression)
        {
            case BinaryNode { NodeType: ExpressionType.Add or ExpressionType.AddChecked } add when IsNumericMember(member):
                if (IsSelf(add.Left, member) && NodeEvaluation.TryValue(add.Right, out var addRight))
                {
                    return new IncrementAssignment(member, addRight!);
                }

                if (IsSelf(add.Right, member) && NodeEvaluation.TryValue(add.Left, out var addLeft))
                {
                    return new IncrementAssignment(member, addLeft!);
                }

                return null;

            case BinaryNode { NodeType: ExpressionType.Subtract or ExpressionType.SubtractChecked } subtract
                when IsNumericMember(member) && IsSelf(subtract.Left, member) && NodeEvaluation.TryValue(subtract.Right, out var delta):
                return new IncrementAssignment(member, Negate(delta!));

            case BinaryNode { NodeType: ExpressionType.Multiply or ExpressionType.MultiplyChecked } multiply when IsNumericMember(member):
                if (IsSelf(multiply.Left, member) && NodeEvaluation.TryValue(multiply.Right, out var factorRight))
                {
                    return new MultiplyAssignment(member, factorRight!);
                }

                if (IsSelf(multiply.Right, member) && NodeEvaluation.TryValue(multiply.Left, out var factorLeft))
                {
                    return new MultiplyAssignment(member, factorLeft!);
                }

                return null;

            case MethodCallNode call:
                return Collection(member, call);

            default:
                return null;
        }
    }

    private static UpdateAssignment? Collection(MemberInfo member, MethodCallNode call)
    {
        if (UnwrapMaterializers(call) is not MethodCallNode { Object: null, Arguments: [var first, var second] } inner
            || !IsEnumerable(inner.Method))
        {
            return null;
        }

        var selfFirst = IsSelf(first, member);
        switch (inner.Method.Name)
        {
            case nameof(System.Linq.Enumerable.Append) when selfFirst && NodeEvaluation.TryValue(second, out var appended):
                return new CollectionAddAssignment(member, [appended], prepend: false, unique: false);
            case nameof(System.Linq.Enumerable.Prepend) when selfFirst && NodeEvaluation.TryValue(second, out var prepended):
                return new CollectionAddAssignment(member, [prepended], prepend: true, unique: false);
            case nameof(System.Linq.Enumerable.Concat) when selfFirst && NodeEvaluation.TryValues(second, out var appendedMany):
                return new CollectionAddAssignment(member, appendedMany, prepend: false, unique: false);
            case nameof(System.Linq.Enumerable.Concat) when IsSelf(second, member) && NodeEvaluation.TryValues(first, out var prependedMany):
                return new CollectionAddAssignment(member, prependedMany, prepend: true, unique: false);
            case nameof(System.Linq.Enumerable.Union) when selfFirst && NodeEvaluation.TryValues(second, out var union):
                return new CollectionAddAssignment(member, union, prepend: false, unique: true);
            case nameof(System.Linq.Enumerable.Except) when selfFirst && NodeEvaluation.TryValues(second, out var removed):
                return new CollectionRemoveAssignment(member, removed);
            default:
                return null;
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Unwraps trailing <c>ToList</c>/<c>ToArray</c>/<c>ToHashSet</c> materializers around a collection expression.</summary>
    private static ExpressionNode UnwrapMaterializers(ExpressionNode node)
    {
        node = NodeEvaluation.Unwrap(node);
        while (node is MethodCallNode { Object: null, Arguments: [var source] } call
               && IsEnumerable(call.Method)
               && call.Method.Name is nameof(System.Linq.Enumerable.ToList) or nameof(System.Linq.Enumerable.ToArray) or nameof(System.Linq.Enumerable.ToHashSet))
        {
            node = NodeEvaluation.Unwrap(source);
        }

        return node;
    }

    /// <summary>Whether the node is the assigned member itself, read from the lambda parameter.</summary>
    private static bool IsSelf(ExpressionNode node, MemberInfo member) =>
        NodeEvaluation.Unwrap(node) is MemberNode { Expression: { } target } access
        && NodeEvaluation.Unwrap(target) is ParameterNode
        && string.Equals(access.Member.Name, member.Name, StringComparison.OrdinalIgnoreCase);

    private static bool IsEnumerable(eQuantic.Linq.Expressions.Metadata.MethodRef method) =>
        method.DeclaringType?.Name is null || method.DeclaringType.Name.EndsWith("Enumerable", StringComparison.Ordinal);

    private static MemberInfo Member(Type entityType, MemberAssignmentNode assignment)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
        return (MemberInfo?)entityType.GetProperty(assignment.Member.Name, flags)
               ?? entityType.GetField(assignment.Member.Name, flags)
               ?? throw new NotSupportedException(
                   $"'{entityType.Name}' has no member '{assignment.Member.Name}' to assign.");
    }

    private static object Negate(object delta) => delta switch
    {
        sbyte value => -value,
        short value => -value,
        int value => -value,
        long value => -value,
        float value => -value,
        double value => -value,
        decimal value => -value,
        _ => throw new NotSupportedException($"Cannot negate a delta of type '{delta.GetType().Name}'."),
    };

    private static bool IsNumericMember(MemberInfo member)
    {
        var type = member is PropertyInfo property ? property.PropertyType : ((FieldInfo)member).FieldType;
        type = Nullable.GetUnderlyingType(type) ?? type;
        return Type.GetTypeCode(type) is TypeCode.SByte or TypeCode.Byte or TypeCode.Int16 or TypeCode.UInt16
            or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64
            or TypeCode.Single or TypeCode.Double or TypeCode.Decimal;
    }
}
