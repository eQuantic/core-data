using System.Text.Json;
using System.Text.Json.Serialization;
using eQuantic.Core.Data.Modeling;

namespace eQuantic.Core.Data.CosmosDb;

/// <summary>
///     Stores an enum declared stored as its labels (<see cref="EnumTypeAttribute" />, or <c>Enum&lt;TEnum&gt;()</c> on
///     the model builder) as a JSON string: each member's <c>[StoredAs]</c> label, or its own name. It is a type-level
///     converter of the provider's serializer, which the SDK's LINQ translation also asks for constants, so a filter
///     comparing a member with an enum value compares labels. An enum declared neither way keeps its number.
/// </summary>
internal sealed class CosmosEnumLabelConverterFactory(IReadOnlyCollection<Type> declared) : JsonConverterFactory
{
    /// <summary>Whether <paramref name="enumType" /> is stored as its labels (declared on the model, or annotated).</summary>
    public bool IsLabelEnum(Type enumType) =>
        enumType.IsEnum && (declared.Contains(enumType) || EnumTypeAttribute.For(enumType) is not null);

    public override bool CanConvert(Type typeToConvert) => IsLabelEnum(typeToConvert);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(CosmosEnumLabelConverter<>).MakeGenericType(typeToConvert))!;
}

/// <summary>Writes an enum as its label and reads it back from it; anything but a label fails loudly, naming the enum.</summary>
/// <typeparam name="TEnum">The enum.</typeparam>
internal sealed class CosmosEnumLabelConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? (TEnum)EnumLabels.Parse(typeof(TEnum), reader.GetString()!)
            : throw new JsonException(
                $"'{typeof(TEnum).Name}' is stored as its labels, and the document holds a JSON {reader.TokenType} where a label belongs.");

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(EnumLabels.Label(value));

    public override TEnum ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        (TEnum)EnumLabels.Parse(typeof(TEnum), reader.GetString()!);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WritePropertyName(EnumLabels.Label(value));
}

/// <summary>
///     Reads a <c>JsonDocument</c> member back as the JSON the document embeds, and a JSON <c>null</c> as a null
///     reference. System.Text.Json's own converter reads <c>null</c> as a document holding <c>null</c>, which would not
///     come back equal to the null that was written.
/// </summary>
internal sealed class CosmosJsonDocumentConverter : JsonConverter<JsonDocument>
{
    public override JsonDocument Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonDocument.ParseValue(ref reader);

    public override void Write(Utf8JsonWriter writer, JsonDocument value, JsonSerializerOptions options) =>
        value.WriteTo(writer);
}

/// <summary>A member the store assigns on insert, as the Cosmos model maps it.</summary>
/// <param name="Property">The entity's property.</param>
/// <param name="StoredName">The document's property name for it.</param>
/// <param name="Value">What the store assigns (<see cref="StoreValue.Sequence" /> or <see cref="StoreValue.Clock" />).</param>
public sealed record CosmosGeneratedMember(System.Reflection.PropertyInfo Property, string StoredName, StoreValue Value)
{
    /// <summary>Sets the store's value on the entity, in the member's own type.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="value">The store's value: a number of the sequence, or the clock's UTC time.</param>
    public void Assign(object entity, object value)
    {
        var type = Nullable.GetUnderlyingType(Property.PropertyType) ?? Property.PropertyType;
        Property.SetValue(entity, value switch
        {
            DateTime utc when type == typeof(DateTimeOffset) => new DateTimeOffset(utc, TimeSpan.Zero),
            DateTime utc => utc,
            _ => Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture),
        });
    }
}
