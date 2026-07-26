using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace eQuantic.Core.Data.Query;

/// <summary>
///     The dialect-agnostic intermediate representation of one set-based update assignment, produced by
///     <see cref="UpdateInterpreter" /> from a member-initialisation update factory and rendered by a provider to
///     its native atomic operation — MongoDB <c>$set</c>/<c>$inc</c>/<c>$mul</c>/<c>$push</c>/<c>$pullAll</c>,
///     Cosmos <c>PatchOperation.Set</c>/<c>Increment</c>/<c>Add</c>, CQL <c>col = ?</c> / <c>col = col + ?</c>.
///     A provider that cannot apply an assignment atomically rejects it with the reason.
/// </summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public abstract class UpdateAssignment
{
    /// <summary>Initializes the assignment for the target member.</summary>
    /// <param name="member">The assigned member.</param>
    protected UpdateAssignment(MemberInfo member) => Member = member;

    /// <summary>The assigned member.</summary>
    public MemberInfo Member { get; }

    /// <summary>The assigned member's name.</summary>
    public string Name => Member.Name;

    /// <summary>The assigned member's CLR type.</summary>
    public Type MemberType => Member is PropertyInfo property ? property.PropertyType : ((FieldInfo)Member).FieldType;

    /// <summary>
    ///     Builds a collection of the member's shape (array, <c>HashSet</c> or <c>List</c> of its element type)
    ///     from loose items, so a renderer can bind or serialize it with the member's own representation.
    /// </summary>
    /// <param name="items">The items.</param>
    protected object ToTypedCollection(IReadOnlyList<object?> items)
    {
        var elementType = ElementTypeOf(MemberType) ?? typeof(object);

        if (MemberType.IsArray)
        {
            var array = Array.CreateInstance(elementType, items.Count);
            for (var index = 0; index < items.Count; index++)
            {
                array.SetValue(items[index], index);
            }

            return array;
        }

        var isSet = typeof(ISet<>).MakeGenericType(elementType).IsAssignableFrom(MemberType);
        var collection = Activator.CreateInstance((isSet ? typeof(HashSet<>) : typeof(List<>)).MakeGenericType(elementType))!;
        var add = collection.GetType().GetMethod("Add")!;
        foreach (var item in items)
        {
            add.Invoke(collection, [item]);
        }

        return collection;
    }

    /// <summary>Whether the member is a set-shaped collection (element membership is unique).</summary>
    public bool IsSetMember()
    {
        var elementType = ElementTypeOf(MemberType);
        return elementType is not null && typeof(ISet<>).MakeGenericType(elementType).IsAssignableFrom(MemberType);
    }

    private static Type? ElementTypeOf(Type type)
    {
        if (type == typeof(string))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        return type.GetInterfaces()
            .Append(type)
            .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }
}

/// <summary>Assigns a constant value: <c>member = value</c>.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class SetAssignment(MemberInfo member, object? value) : UpdateAssignment(member)
{
    /// <summary>The value to set.</summary>
    public object? Value { get; } = value;
}

/// <summary>Adds a constant delta to the member's current value: <c>member = member + delta</c> (delta is negative for a subtraction).</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class IncrementAssignment(MemberInfo member, object delta) : UpdateAssignment(member)
{
    /// <summary>The delta (negative for a subtraction).</summary>
    public object Delta { get; } = delta;
}

/// <summary>Multiplies the member's current value by a constant factor: <c>member = member * factor</c>.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class MultiplyAssignment(MemberInfo member, object factor) : UpdateAssignment(member)
{
    /// <summary>The factor.</summary>
    public object Factor { get; } = factor;
}

/// <summary>Adds items to the member's current collection (append, prepend, or unique/set union).</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class CollectionAddAssignment(MemberInfo member, IReadOnlyList<object?> items, bool prepend, bool unique)
    : UpdateAssignment(member)
{
    /// <summary>The items to add.</summary>
    public IReadOnlyList<object?> Items { get; } = items;

    /// <summary>Whether the items go at the front instead of the end.</summary>
    public bool Prepend { get; } = prepend;

    /// <summary>Whether membership is unique (a set union rather than an append).</summary>
    public bool Unique { get; } = unique;

    /// <summary>The items shaped as the member's own collection type.</summary>
    public object ToTypedCollection() => ToTypedCollection(Items);
}

/// <summary>Removes items from the member's current collection.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class CollectionRemoveAssignment(MemberInfo member, IReadOnlyList<object?> items) : UpdateAssignment(member)
{
    /// <summary>The items to remove.</summary>
    public IReadOnlyList<object?> Items { get; } = items;

    /// <summary>The items shaped as the member's own collection type.</summary>
    public object ToTypedCollection() => ToTypedCollection(Items);
}
