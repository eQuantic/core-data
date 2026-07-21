using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace eQuantic.Core.Data.Query;

/// <summary>
///     Interprets a member-initialisation update factory (<c>x =&gt; new TEntity { Status = "Closed", N = x.N + 1,
///     Tags = x.Tags.Append("vip").ToList() }</c>) into the dialect-agnostic <see cref="UpdateAssignment" /> model.
///     A constant assignment becomes a <see cref="SetAssignment" />; an assignment that reads the entity is
///     recognised when a store can apply it atomically — <c>member ± n</c> and <c>member * n</c> (numeric), and
///     <c>Append</c>/<c>Prepend</c>/<c>Concat</c>/<c>Union</c>/<c>Except</c> over the same collection member
///     (optionally wrapped in <c>ToList</c>/<c>ToArray</c>/<c>ToHashSet</c>). Anything else is rejected with the
///     supported shapes, so an update never silently degrades to a read-modify-write.
/// </summary>
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

        if (updateFactory.Body is not MemberInitExpression memberInit)
        {
            throw new NotSupportedException(
                "UpdateMany expects a member-initialisation update, e.g. x => new " + typeof(TEntity).Name + " { Status = \"Closed\" }.");
        }

        var parameter = updateFactory.Parameters[0];
        var assignments = new List<UpdateAssignment>();

        foreach (var binding in memberInit.Bindings)
        {
            if (binding is not MemberAssignment assignment)
            {
                throw new NotSupportedException(
                    $"Only member assignments are supported in an UpdateMany update; got '{binding.BindingType}'.");
            }

            assignments.Add(Assignment(assignment, parameter));
        }

        if (assignments.Count == 0)
        {
            throw new NotSupportedException("The UpdateMany update assigns no fields.");
        }

        return assignments;
    }

    private static UpdateAssignment Assignment(MemberAssignment assignment, ParameterExpression parameter)
    {
        var expression = Strip(assignment.Expression);
        if (!References(expression, parameter))
        {
            return new SetAssignment(assignment.Member, Evaluate(expression));
        }

        return Computed(assignment.Member, expression, parameter)
               ?? throw new NotSupportedException(
                   $"The assignment to '{assignment.Member.Name}' references the entity in a shape no store can apply atomically. " +
                   "Supported computed shapes: member + n, member - n, member * n (numeric members), and " +
                   "Append/Prepend/Concat/Union/Except over the same collection member.");
    }

    private static UpdateAssignment? Computed(MemberInfo member, Expression expression, ParameterExpression parameter)
    {
        switch (expression)
        {
            case BinaryExpression { NodeType: ExpressionType.Add or ExpressionType.AddChecked } add when IsNumericMember(member):
                if (IsSelf(add.Left, member, parameter) && !References(add.Right, parameter))
                {
                    return new IncrementAssignment(member, Evaluate(add.Right)!);
                }

                if (IsSelf(add.Right, member, parameter) && !References(add.Left, parameter))
                {
                    return new IncrementAssignment(member, Evaluate(add.Left)!);
                }

                return null;

            case BinaryExpression { NodeType: ExpressionType.Subtract or ExpressionType.SubtractChecked } subtract
                when IsNumericMember(member) && IsSelf(subtract.Left, member, parameter) && !References(subtract.Right, parameter):
                return new IncrementAssignment(member, Evaluate(Expression.Negate(subtract.Right))!);

            case BinaryExpression { NodeType: ExpressionType.Multiply or ExpressionType.MultiplyChecked } multiply when IsNumericMember(member):
                if (IsSelf(multiply.Left, member, parameter) && !References(multiply.Right, parameter))
                {
                    return new MultiplyAssignment(member, Evaluate(multiply.Right)!);
                }

                if (IsSelf(multiply.Right, member, parameter) && !References(multiply.Left, parameter))
                {
                    return new MultiplyAssignment(member, Evaluate(multiply.Left)!);
                }

                return null;

            default:
                return Collection(member, expression, parameter);
        }
    }

    private static UpdateAssignment? Collection(MemberInfo member, Expression expression, ParameterExpression parameter)
    {
        expression = UnwrapMaterializers(expression);
        if (expression is not MethodCallExpression { Object: null } call || call.Method.DeclaringType != typeof(Enumerable))
        {
            return null;
        }

        return call.Method.Name switch
        {
            nameof(Enumerable.Append) when IsSelf(call.Arguments[0], member, parameter) && !References(call.Arguments[1], parameter) =>
                new CollectionAddAssignment(member, [Evaluate(call.Arguments[1])], prepend: false, unique: false),
            nameof(Enumerable.Prepend) when IsSelf(call.Arguments[0], member, parameter) && !References(call.Arguments[1], parameter) =>
                new CollectionAddAssignment(member, [Evaluate(call.Arguments[1])], prepend: true, unique: false),
            nameof(Enumerable.Concat) when IsSelf(call.Arguments[0], member, parameter) && !References(call.Arguments[1], parameter) =>
                new CollectionAddAssignment(member, Items(call.Arguments[1]), prepend: false, unique: false),
            nameof(Enumerable.Concat) when IsSelf(call.Arguments[1], member, parameter) && !References(call.Arguments[0], parameter) =>
                new CollectionAddAssignment(member, Items(call.Arguments[0]), prepend: true, unique: false),
            nameof(Enumerable.Union) when IsSelf(call.Arguments[0], member, parameter) && !References(call.Arguments[1], parameter) =>
                new CollectionAddAssignment(member, Items(call.Arguments[1]), prepend: false, unique: true),
            nameof(Enumerable.Except) when IsSelf(call.Arguments[0], member, parameter) && !References(call.Arguments[1], parameter) =>
                new CollectionRemoveAssignment(member, Items(call.Arguments[1])),
            _ => null,
        };
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Strips compiler-inserted conversions.</summary>
    private static Expression Strip(Expression expression) =>
        expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary
            ? Strip(unary.Operand)
            : expression;

    /// <summary>Unwraps trailing <c>ToList</c>/<c>ToArray</c>/<c>ToHashSet</c> materializers around a collection expression.</summary>
    private static Expression UnwrapMaterializers(Expression expression)
    {
        expression = Strip(expression);
        while (expression is MethodCallExpression { Object: null, Arguments.Count: 1 } call
               && call.Method.DeclaringType == typeof(Enumerable)
               && call.Method.Name is nameof(Enumerable.ToList) or nameof(Enumerable.ToArray) or nameof(Enumerable.ToHashSet))
        {
            expression = Strip(call.Arguments[0]);
        }

        return expression;
    }

    /// <summary>Whether the expression is the assigned member itself, read from the lambda parameter.</summary>
    private static bool IsSelf(Expression expression, MemberInfo member, ParameterExpression parameter) =>
        Strip(expression) is MemberExpression { Expression: ParameterExpression root } access
        && ReferenceEquals(root, parameter)
        && access.Member.Name == member.Name;

    private static bool References(Expression expression, ParameterExpression parameter)
    {
        var finder = new ParameterFinder(parameter);
        finder.Visit(expression);
        return finder.Found;
    }

    private static object? Evaluate(Expression expression) =>
        expression is ConstantExpression constant
            ? constant.Value
            : Expression.Lambda(expression).Compile().DynamicInvoke();

    private static IReadOnlyList<object?> Items(Expression expression) =>
        Evaluate(expression) is IEnumerable sequence and not string
            ? sequence.Cast<object?>().ToList()
            : throw new NotSupportedException("A collection update requires a constant sequence of items.");

    private static bool IsNumericMember(MemberInfo member)
    {
        var type = member is PropertyInfo property ? property.PropertyType : ((FieldInfo)member).FieldType;
        type = Nullable.GetUnderlyingType(type) ?? type;
        return Type.GetTypeCode(type) is TypeCode.SByte or TypeCode.Byte or TypeCode.Int16 or TypeCode.UInt16
            or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64
            or TypeCode.Single or TypeCode.Double or TypeCode.Decimal;
    }

    private sealed class ParameterFinder(ParameterExpression parameter) : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == parameter)
            {
                Found = true;
            }

            return base.VisitParameter(node);
        }
    }
}
