using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using eQuantic.Core.Data.Modeling;
using Microsoft.Azure.Cosmos;

namespace eQuantic.Core.Data.CosmosDb;

/// <summary>
///     The provider's document serializer: System.Text.Json with web (camelCase) defaults, plus the store-neutral
///     modeling vocabulary — <c>[StoredAs]</c> renames the element, <c>[Unmapped]</c> keeps the member out of the
///     document, and the model's <c>Converts</c> registrations translate values both ways. It extends
///     <see cref="CosmosLinqSerializer" /> so the SDK's LINQ translation asks <b>this</b> serializer for member
///     names: a renamed member filters, sorts and projects against its stored name — the query can never
///     desynchronize from the document.
/// </summary>
public sealed class CosmosEntitySerializer : CosmosLinqSerializer
{
    private readonly JsonSerializerOptions _options;

    /// <summary>Initializes the serializer over the given options (see <see cref="CosmosClientFactory" />).</summary>
    /// <param name="options">The System.Text.Json options.</param>
    public CosmosEntitySerializer(JsonSerializerOptions options) => _options = options;

    /// <summary>
    ///     Builds the provider's serializer options: web defaults, the modeling-annotation contract (renames and
    ///     exclusions), and the model's value converters when a model is given.
    /// </summary>
    /// <param name="model">The Cosmos model whose <c>Converts</c> registrations apply, or <c>null</c>.</param>
    public static JsonSerializerOptions BuildOptions(CosmosModel? model = null)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { ApplyModelingAnnotations } },
        };

        if (model is not null)
        {
            foreach (var converter in model.Converters)
            {
                options.Converters.Add(converter);
            }
        }

        return options;
    }

    /// <summary>The element name the SDK's LINQ translation uses for <paramref name="memberInfo" />.</summary>
    public override string SerializeMemberName(MemberInfo memberInfo)
    {
        if (memberInfo.GetCustomAttributes(typeof(UnmappedAttribute), inherit: true).Length > 0)
        {
            throw new NotSupportedException(
                $"'{memberInfo.DeclaringType?.Name}.{memberInfo.Name}' is [Unmapped]; it does not exist in the stored " +
                "document, so no query can filter, sort or project on it.");
        }

        return CosmosNaming.StoredName(memberInfo);
    }

    /// <inheritdoc />
    public override T FromStream<T>(Stream stream)
    {
        if (typeof(Stream).IsAssignableFrom(typeof(T)))
        {
            return (T)(object)stream;
        }

        using (stream)
        {
            return JsonSerializer.Deserialize<T>(stream, _options)!;
        }
    }

    /// <inheritdoc />
    public override Stream ToStream<T>(T input)
    {
        var stream = new MemoryStream();
        JsonSerializer.Serialize(stream, input, _options);
        stream.Position = 0;
        return stream;
    }

    /// <summary>
    ///     The serialization contract for the annotations: <c>[Unmapped]</c> members leave the contract entirely
    ///     (they neither write nor read), <c>[StoredAs]</c> members take their stored name verbatim.
    /// </summary>
    private static void ApplyModelingAnnotations(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        for (var index = typeInfo.Properties.Count - 1; index >= 0; index--)
        {
            var property = typeInfo.Properties[index];
            if (property.AttributeProvider is not MemberInfo member)
            {
                continue;
            }

            if (member.GetCustomAttributes(typeof(UnmappedAttribute), inherit: true).Length > 0)
            {
                typeInfo.Properties.RemoveAt(index);
                continue;
            }

            if (member.GetCustomAttributes(typeof(StoredAsAttribute), inherit: true) is [StoredAsAttribute stored, ..])
            {
                property.Name = stored.Name;
            }
        }
    }
}
