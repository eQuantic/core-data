using System.Text.Json;
using eQuantic.Core.Data.Modeling;

namespace eQuantic.Core.Data.Cassandra;

/// <summary>
///     Converts a member's value to the form its column stores, and back: an enum stored as its labels as its label
///     (<c>text</c>), any other enum as its number, a JSON member as its JSON text. Every value the provider binds for
///     a column — writes, filters, set-based updates — goes through <see cref="ToStored" />, so a comparison with an
///     enum member compares labels, whichever shape the compiler gave it (<c>(int)x.State == 1</c> included).
/// </summary>
internal static class CassandraValues
{
    /// <summary>The value as the column stores it.</summary>
    public static object? ToStored(CassandraColumn? column, object? value)
    {
        if (value is null || column is null)
        {
            return value;
        }

        var type = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;
        if (type.IsEnum)
        {
            // C# compiles a comparison with an enum member into one of numbers, so the value may arrive as the number.
            var member = value as Enum ?? (Enum)Enum.ToObject(type, value);
            return column.CqlType == "text"
                ? EnumLabels.Label(member)
                : Convert.ChangeType(member, Enum.GetUnderlyingType(type), System.Globalization.CultureInfo.InvariantCulture);
        }

        if (JsonMembers.Is(type))
        {
            return value is JsonElement { ValueKind: JsonValueKind.Undefined } ? null : JsonMembers.Text(value);
        }

        return value;
    }

    /// <summary>The CLR type the driver reads the column as (the stored form's).</summary>
    public static Type StoredType(CassandraColumn column, Type memberType)
    {
        var type = Nullable.GetUnderlyingType(memberType) ?? memberType;
        if (type.IsEnum)
        {
            return column.CqlType == "text" ? typeof(string) : Enum.GetUnderlyingType(type);
        }

        return JsonMembers.Is(type) ? typeof(string) : memberType;
    }

    /// <summary>The member's value from what the column stores.</summary>
    public static object? FromStored(Type memberType, object? stored)
    {
        if (stored is null)
        {
            return null;
        }

        var type = Nullable.GetUnderlyingType(memberType) ?? memberType;
        if (type.IsEnum)
        {
            return stored is string label ? EnumLabels.Parse(type, label) : Enum.ToObject(type, stored);
        }

        return stored is string json && JsonMembers.Is(type) ? JsonMembers.Parse(json, type) : stored;
    }
}
