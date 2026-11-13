using System.Text.Json;
using System.Text.Json.Nodes;
using eQuantic.Core.Data.Modeling;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace eQuantic.Core.Data.MongoDb;

/// <summary>A serializer of a stored-value kind the model describes (labels, embedded JSON) — what <c>Explain()</c> names.</summary>
internal interface IMongoStoredValueSerializer
{
    /// <summary>How the member is stored, for <c>Explain()</c>.</summary>
    string Describe();
}

/// <summary>
///     Stores an enum declared stored as its labels (<see cref="EnumTypeAttribute" />, or <c>Enum&lt;TEnum&gt;()</c> on
///     the model builder) as a BSON string: each member's <c>[StoredAs]</c> label, or its own name. Filters, sorts and
///     set-based updates go through the same serializer, so a comparison with a member compares labels. A document
///     that holds anything other than a label fails loudly, naming the enum.
/// </summary>
/// <typeparam name="TEnum">The enum.</typeparam>
internal sealed class MongoEnumLabelSerializer<TEnum> : StructSerializerBase<TEnum>, IMongoStoredValueSerializer
    where TEnum : struct, Enum
{
    public override TEnum Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        var type = reader.GetCurrentBsonType();
        if (type != BsonType.String)
        {
            throw new FormatException(
                $"'{typeof(TEnum).Name}' is stored as its labels, and the document holds a BSON {type} where a label belongs.");
        }

        return (TEnum)EnumLabels.Parse(typeof(TEnum), reader.ReadString());
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, TEnum value) =>
        context.Writer.WriteString(EnumLabels.Label(value));

    public string Describe() => "labels: " + string.Join(", ", EnumLabels.Of(typeof(TEnum)).Values);

    public override bool Equals(object? obj) => obj is MongoEnumLabelSerializer<TEnum>;

    public override int GetHashCode() => typeof(MongoEnumLabelSerializer<TEnum>).GetHashCode();
}

/// <summary>
///     Stores a JSON member (<c>JsonDocument</c>, <c>JsonElement</c>, <c>JsonNode</c> and its subtypes) as the BSON value
///     its JSON is — an object as an embedded document, an array as a BSON array, a scalar as the BSON scalar — and
///     reads it back into the member's type, equal. <c>null</c> stays <c>null</c>.
/// </summary>
/// <typeparam name="TJson">The member's JSON type.</typeparam>
internal sealed class MongoJsonMemberSerializer<TJson> : SerializerBase<TJson>, IMongoStoredValueSerializer
{
    public override TJson Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var value = BsonValueSerializer.Instance.Deserialize(context);
        if (value.IsBsonNull)
        {
            return default!;
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, value);
        }

        return (TJson)JsonMembers.Parse(System.Text.Encoding.UTF8.GetString(buffer.ToArray()), typeof(TJson))!;
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, TJson value)
    {
        // An unset JsonElement (default, Undefined) holds no JSON: stored as null, as a null reference is.
        if (value is null or JsonElement { ValueKind: JsonValueKind.Undefined })
        {
            context.Writer.WriteNull();
            return;
        }

        using var document = JsonDocument.Parse(JsonMembers.Text(value)!);
        BsonValueSerializer.Instance.Serialize(context, ToBson(document.RootElement));
    }

    public string Describe() => "embedded JSON";

    public override bool Equals(object? obj) => obj is MongoJsonMemberSerializer<TJson>;

    public override int GetHashCode() => typeof(MongoJsonMemberSerializer<TJson>).GetHashCode();

    private static BsonValue ToBson(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var document = new BsonDocument();
                foreach (var property in element.EnumerateObject())
                {
                    document.Add(property.Name, ToBson(property.Value));
                }

                return document;
            }
            case JsonValueKind.Array:
                return new BsonArray(element.EnumerateArray().Select(ToBson));
            case JsonValueKind.String:
                return new BsonString(element.GetString());
            case JsonValueKind.Number:
                if (element.TryGetInt32(out var int32))
                {
                    return new BsonInt32(int32);
                }

                if (element.TryGetInt64(out var int64))
                {
                    return new BsonInt64(int64);
                }

                // A number a double would round (more significant digits than it keeps) is kept as a decimal.
                return element.TryGetDecimal(out var number) && (decimal)(double)number != number
                    ? new BsonDecimal128(number)
                    : new BsonDouble(element.GetDouble());
            case JsonValueKind.True:
                return BsonBoolean.True;
            case JsonValueKind.False:
                return BsonBoolean.False;
            default:
                return BsonNull.Value;
        }
    }

    private static void Write(Utf8JsonWriter writer, BsonValue value)
    {
        switch (value.BsonType)
        {
            case BsonType.Document:
                writer.WriteStartObject();
                foreach (var element in value.AsBsonDocument)
                {
                    writer.WritePropertyName(element.Name);
                    Write(writer, element.Value);
                }

                writer.WriteEndObject();
                break;
            case BsonType.Array:
                writer.WriteStartArray();
                foreach (var item in value.AsBsonArray)
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;
            case BsonType.String:
                writer.WriteStringValue(value.AsString);
                break;
            case BsonType.Int32:
                writer.WriteNumberValue(value.AsInt32);
                break;
            case BsonType.Int64:
                writer.WriteNumberValue(value.AsInt64);
                break;
            case BsonType.Double:
                writer.WriteNumberValue(value.AsDouble);
                break;
            case BsonType.Decimal128:
                writer.WriteNumberValue(Decimal128.ToDecimal(value.AsDecimal128));
                break;
            case BsonType.Boolean:
                writer.WriteBooleanValue(value.AsBoolean);
                break;
            case BsonType.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new FormatException(
                    $"A JSON member cannot hold the BSON {value.BsonType} the document stores; it holds only what JSON has.");
        }
    }
}

/// <summary>
///     Hands the driver the serializers of the shared stored-value vocabulary: labels for an enum annotated
///     <see cref="EnumTypeAttribute" />, and embedded BSON for the JSON member types. An enum without the annotation
///     falls through to the driver, which stores its number.
/// </summary>
internal sealed class MongoStoredValueSerializationProvider : IBsonSerializationProvider
{
    public IBsonSerializer? GetSerializer(Type type)
    {
        if (type.IsEnum && EnumTypeAttribute.For(type) is not null)
        {
            return (IBsonSerializer)Activator.CreateInstance(typeof(MongoEnumLabelSerializer<>).MakeGenericType(type))!;
        }

        return Nullable.GetUnderlyingType(type) is null && JsonMembers.Is(type)
            ? (IBsonSerializer)Activator.CreateInstance(typeof(MongoJsonMemberSerializer<>).MakeGenericType(type))!
            : null;
    }
}
