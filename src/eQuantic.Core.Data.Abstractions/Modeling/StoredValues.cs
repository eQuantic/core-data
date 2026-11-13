using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace eQuantic.Core.Data.Modeling;

/// <summary>
///     Declares that an enum is stored as its <b>labels</b>, and that the store holds no other value for it: each
///     member is written as its <c>[StoredAs]</c> label (or its own name), read back from it and filtered by it.
///     Each provider uses its own mechanism: a PostgreSQL enum type named <see cref="Name" />, a MySQL/MariaDB
///     <c>ENUM(...)</c> column, a SQL Server text column with a <c>CHECK</c> on the labels, and the label as a
///     string on MongoDB, Cosmos DB and Cassandra (which have no enum type to restrict it).
///     <para>An enum without it is stored as its number, as it always was.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Enum)]
public sealed class EnumTypeAttribute : Attribute
{
    /// <summary>Declares the enum with the store's naming convention for its type name.</summary>
    public EnumTypeAttribute()
    {
    }

    /// <summary>Declares the enum with an explicit type name (where the store has enum types).</summary>
    /// <param name="name">The type name, unquoted.</param>
    public EnumTypeAttribute(string name) => Name = name;

    /// <summary>The store's type name; <c>null</c> takes the store's naming convention.</summary>
    public string? Name { get; }

    /// <summary>The declaration on <paramref name="enumType" />, or <c>null</c> when it is stored as its number.</summary>
    /// <param name="enumType">The enum.</param>
    public static EnumTypeAttribute? For(Type enumType) =>
        GetCustomAttribute(enumType, typeof(EnumTypeAttribute)) as EnumTypeAttribute;
}

/// <summary>What the store assigns to a store-generated member on insert.</summary>
public enum StoreValue
{
    /// <summary>Inferred from the member: <see cref="Sequence" /> for an integral one, <see cref="Clock" /> for a date.</summary>
    Inferred,

    /// <summary>The next number of a sequence the store keeps (an integral member): unique, and growing with each insert.</summary>
    Sequence,

    /// <summary>The store's clock when the insert ran (a <c>DateTime</c> or <c>DateTimeOffset</c> member), in UTC.</summary>
    Clock,
}

/// <summary>
///     Declares a member whose value the <b>store</b> assigns on insert: the insert leaves it to the store, the
///     value is read back onto the entity, and an update never writes it. Each provider uses its own mechanism (an
///     identity or a sequence and a clock default on relational stores, a counter and the server's clock on
///     MongoDB, Cosmos DB and Cassandra); <c>Explain()</c> names it.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class StoreGeneratedAttribute : Attribute
{
    /// <summary>Declares the member store-generated, its kind inferred from its type.</summary>
    public StoreGeneratedAttribute()
    {
    }

    /// <summary>Declares the member store-generated with an explicit kind.</summary>
    /// <param name="value">What the store assigns.</param>
    public StoreGeneratedAttribute(StoreValue value) => Value = value;

    /// <summary>What the store assigns.</summary>
    public StoreValue Value { get; } = StoreValue.Inferred;

    /// <summary>
    ///     What a store-generated member of <paramref name="memberType" /> receives, with <see cref="StoreValue.Inferred" />
    ///     resolved; throws when the kind cannot apply to the type, naming the member.
    /// </summary>
    /// <param name="value">The declared kind.</param>
    /// <param name="memberType">The member's type.</param>
    /// <param name="member">The member, for the message.</param>
    public static StoreValue Resolve(StoreValue value, Type memberType, string member)
    {
        var type = Nullable.GetUnderlyingType(memberType) ?? memberType;
        var integral = type == typeof(int) || type == typeof(long) || type == typeof(short);
        var date = type == typeof(DateTime) || type == typeof(DateTimeOffset);
        var resolved = value switch
        {
            StoreValue.Inferred when integral => StoreValue.Sequence,
            StoreValue.Inferred when date => StoreValue.Clock,
            _ => value,
        };

        return resolved switch
        {
            StoreValue.Sequence when integral => resolved,
            StoreValue.Clock when date => resolved,
            _ => throw new InvalidOperationException(
                $"'{member}' is declared store-generated, and a store can only generate a sequence for an integral " +
                "member (int, long, short) or its clock for a date (DateTime, DateTimeOffset); " +
                $"'{type.Name}' is neither."),
        };
    }
}

/// <summary>The labels an enum declared with <see cref="EnumTypeAttribute" /> is stored as, both ways.</summary>
public static class EnumLabels
{
    private static readonly ConcurrentDictionary<Type, (IReadOnlyDictionary<string, string> ByMember, IReadOnlyDictionary<string, object> ByLabel)> Cache = new();

    /// <summary>Each member name and its label (its <c>[StoredAs]</c> name, or its own), in declaration order.</summary>
    /// <param name="enumType">The enum.</param>
    public static IReadOnlyDictionary<string, string> Of(Type enumType) => Describe(enumType).ByMember;

    /// <summary>The label <paramref name="value" /> is stored as.</summary>
    /// <param name="value">An enum value.</param>
    public static string Label(Enum value)
    {
        var name = Enum.GetName(value.GetType(), value)
                   ?? throw new InvalidOperationException(
                       $"{value} is not a member of '{value.GetType().Name}', so it has no label to be stored as.");
        return Describe(value.GetType()).ByMember[name];
    }

    /// <summary>The member of <paramref name="enumType" /> a stored label names; throws on a label it does not have.</summary>
    /// <param name="enumType">The enum.</param>
    /// <param name="label">The stored label.</param>
    public static object Parse(Type enumType, string label) =>
        Describe(enumType).ByLabel.TryGetValue(label, out var member)
            ? member
            : throw new InvalidOperationException($"'{label}' is not a label of '{enumType.Name}'.");

    private static (IReadOnlyDictionary<string, string> ByMember, IReadOnlyDictionary<string, object> ByLabel) Describe(Type enumType) =>
        Cache.GetOrAdd(enumType, type =>
        {
            if (!type.IsEnum)
            {
                throw new ArgumentException($"'{type.Name}' is not an enum.", nameof(enumType));
            }

            var byMember = new Dictionary<string, string>(StringComparer.Ordinal);
            var byLabel = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var label = field.GetCustomAttribute<StoredAsAttribute>()?.Name ?? field.Name;
                if (string.IsNullOrWhiteSpace(label))
                {
                    throw new InvalidOperationException($"The member '{type.Name}.{field.Name}' has an empty stored label.");
                }

                if (byLabel.ContainsKey(label))
                {
                    throw new InvalidOperationException(
                        $"Two members of '{type.Name}' are stored as '{label}'; each label names one member.");
                }

                byMember[field.Name] = label;
                byLabel[label] = field.GetValue(null)!;
            }

            return (byMember, byLabel);
        });
}

/// <summary>
///     The JSON document types a member can hold — <c>JsonDocument</c>, <c>JsonElement</c>, <c>JsonNode</c> and its
///     subtypes — whatever JSON they hold: an object, an array or a scalar. Every provider stores them in its own
///     JSON form (<c>jsonb</c>, <c>JSON</c>, text checked by <c>ISJSON</c>, an embedded BSON value, an embedded
///     JSON value, JSON text) and gives them back equal.
/// </summary>
public static class JsonMembers
{
    /// <summary>Whether a member of <paramref name="type" /> holds a JSON document (nullable <c>JsonElement</c> included).</summary>
    /// <param name="type">The member's type.</param>
    public static bool Is(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying == typeof(JsonDocument) || underlying == typeof(JsonElement) || typeof(JsonNode).IsAssignableFrom(underlying);
    }

    /// <summary>Whether <paramref name="value" /> is a JSON document a member of one of those types holds.</summary>
    /// <param name="value">The value.</param>
    public static bool IsValue(object? value) => value is JsonDocument or JsonElement or JsonNode;

    /// <summary>The JSON text of a JSON member's value (<c>null</c> stays <c>null</c>).</summary>
    /// <param name="value">A <c>JsonDocument</c>, <c>JsonElement</c> or <c>JsonNode</c>.</param>
    public static string? Text(object? value) => value switch
    {
        null => null,
        JsonDocument document => document.RootElement.GetRawText(),
        JsonElement element => element.GetRawText(),
        JsonNode node => node.ToJsonString(),
        _ => throw new ArgumentException($"'{value.GetType().Name}' is not a JSON member's value.", nameof(value)),
    };

    /// <summary>Parses JSON text read from the store into the member's type.</summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="type">The member's type.</param>
    public static object? Parse(string json, Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(JsonDocument))
        {
            return JsonDocument.Parse(json);
        }

        if (underlying == typeof(JsonElement))
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        var node = JsonNode.Parse(json);
        return node is null || underlying.IsInstanceOfType(node)
            ? node
            : throw new InvalidOperationException(
                $"The stored JSON is a {node.GetType().Name}, which a '{underlying.Name}' member cannot hold.");
    }
}
