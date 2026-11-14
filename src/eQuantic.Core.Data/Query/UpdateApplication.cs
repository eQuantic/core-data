using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace eQuantic.Core.Data.Query;

/// <summary>
///     Applies <see cref="UpdateAssignment" />s to an entity's members in memory: the value a set-based update writes
///     into a row, computed from the row as read. A store without an atomic operator for an assignment (Cassandra has
///     no <c>col = col + 1</c> outside counter columns) writes that value under a compare-and-set on what it read, so the
///     result is the same as the operator's. <b>Provider SPI</b>: application code never needs to call it.
/// </summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class UpdateApplication
{
    /// <summary>Applies the assignment to the entity's member.</summary>
    /// <param name="entity">The entity, holding the row as read.</param>
    /// <param name="assignment">The assignment.</param>
    public static void Apply(object entity, UpdateAssignment assignment)
    {
        if (entity is null)
        {
            throw new ArgumentNullException(nameof(entity));
        }

        var current = Read(entity, assignment.Member);
        Write(entity, assignment.Member, assignment switch
        {
            SetAssignment set => set.Value,
            IncrementAssignment increment => Arithmetic(current, increment.Delta, assignment, add: true),
            MultiplyAssignment multiply => Arithmetic(current, multiply.Factor, assignment, add: false),
            CollectionAddAssignment add => Added(current, add),
            CollectionRemoveAssignment remove => Removed(current, remove),
            _ => throw new NotSupportedException($"The assignment '{assignment.GetType().Name}' has no in-memory form."),
        });
    }

    private static object? Arithmetic(object? current, object operand, UpdateAssignment assignment, bool add)
    {
        var type = Nullable.GetUnderlyingType(assignment.MemberType) ?? assignment.MemberType;

        // A null number reads as nothing to add to: SQL's NULL + 1 is NULL, and so is this.
        if (current is null)
        {
            return null;
        }

        object result = Type.GetTypeCode(type) switch
        {
            TypeCode.Decimal => add
                ? Convert.ToDecimal(current, CultureInfo.InvariantCulture) + Convert.ToDecimal(operand, CultureInfo.InvariantCulture)
                : Convert.ToDecimal(current, CultureInfo.InvariantCulture) * Convert.ToDecimal(operand, CultureInfo.InvariantCulture),
            TypeCode.Single or TypeCode.Double => add
                ? Convert.ToDouble(current, CultureInfo.InvariantCulture) + Convert.ToDouble(operand, CultureInfo.InvariantCulture)
                : Convert.ToDouble(current, CultureInfo.InvariantCulture) * Convert.ToDouble(operand, CultureInfo.InvariantCulture),
            TypeCode.UInt64 => add
                ? Convert.ToUInt64(current, CultureInfo.InvariantCulture) + Convert.ToUInt64(operand, CultureInfo.InvariantCulture)
                : Convert.ToUInt64(current, CultureInfo.InvariantCulture) * Convert.ToUInt64(operand, CultureInfo.InvariantCulture),
            _ => add
                ? checked(Convert.ToInt64(current, CultureInfo.InvariantCulture) + Convert.ToInt64(operand, CultureInfo.InvariantCulture))
                : checked(Convert.ToInt64(current, CultureInfo.InvariantCulture) * Convert.ToInt64(operand, CultureInfo.InvariantCulture)),
        };

        return Convert.ChangeType(result, type, CultureInfo.InvariantCulture);
    }

    private static object? Added(object? current, CollectionAddAssignment add)
    {
        var items = Items(current);
        var incoming = add.Unique ? add.Items.Where(item => !items.Contains(item)).Distinct().ToList() : add.Items.ToList();
        var result = add.Prepend ? incoming.Concat(items).ToList() : items.Concat(incoming).ToList();
        return Shaped(add, result);
    }

    private static object? Removed(object? current, CollectionRemoveAssignment remove) =>
        Shaped(remove, Items(current).Where(item => !remove.Items.Contains(item)).ToList());

    private static List<object?> Items(object? collection) =>
        collection is IEnumerable enumerable and not string ? enumerable.Cast<object?>().ToList() : [];

    /// <summary>The items as the member's own collection type (array, set or list).</summary>
    private static object Shaped(UpdateAssignment assignment, IReadOnlyList<object?> items) =>
        new CollectionAddAssignment(assignment.Member, items, prepend: false, unique: false).ToTypedCollection();

    private static object? Read(object entity, MemberInfo member) => member switch
    {
        PropertyInfo property => property.GetValue(entity),
        FieldInfo field => field.GetValue(entity),
        _ => throw new NotSupportedException($"'{member.Name}' is neither a property nor a field."),
    };

    private static void Write(object entity, MemberInfo member, object? value)
    {
        switch (member)
        {
            case PropertyInfo property:
                property.SetValue(entity, value);
                break;
            case FieldInfo field:
                field.SetValue(entity, value);
                break;
        }
    }
}
