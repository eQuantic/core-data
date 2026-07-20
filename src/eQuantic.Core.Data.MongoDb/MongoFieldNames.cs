using System.Linq.Expressions;
using System.Reflection;
using eQuantic.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

namespace eQuantic.Core.Data.MongoDb;

/// <summary>
///     Resolves typed member selectors to their stored BSON representation — element names and values — using
///     the registered class maps, so <c>[BsonElement]</c> / <c>[BsonRepresentation]</c> / custom serializers are
///     honoured and a migration or set-based update never spells a field or encodes a value by hand.
/// </summary>
internal static class MongoFieldNames
{
    /// <summary>The dotted BSON element path for a member selector (e.g. <c>x =&gt; x.Address.City</c> → <c>address.city</c>).</summary>
    public static string Resolve(Type entityType, LambdaExpression selector)
    {
        var segments = selector.GetMemberPath().Split('.');
        var names = new List<string>(segments.Length);
        var currentType = entityType;

        foreach (var segment in segments)
        {
            names.Add(ElementName(currentType, segment, out var memberType));
            currentType = memberType;
        }

        return string.Join(".", names);
    }

    /// <summary>The BSON element name for a single member.</summary>
    public static string Resolve(Type entityType, MemberInfo member) => ElementName(entityType, member.Name, out _);

    /// <summary>Encodes a CLR value to the <see cref="BsonValue" /> the member is stored as, honouring its serializer.</summary>
    public static BsonValue Serialize(Type entityType, MemberInfo member, object? value)
    {
        if (value is null)
        {
            return BsonNull.Value;
        }

        var memberMap = MemberMap(entityType, member.Name);
        if (memberMap is null)
        {
            return BsonValue.Create(value);
        }

        var document = new BsonDocument();
        using var writer = new BsonDocumentWriter(document);
        writer.WriteStartDocument();
        writer.WriteName(memberMap.ElementName);
        memberMap.GetSerializer().Serialize(BsonSerializationContext.CreateRoot(writer), value);
        writer.WriteEndDocument();
        return document[memberMap.ElementName];
    }

    private static string ElementName(Type declaringType, string memberName, out Type memberType)
    {
        var memberMap = MemberMap(declaringType, memberName);
        if (memberMap is not null)
        {
            memberType = memberMap.MemberType;
            return memberMap.ElementName;
        }

        memberType = declaringType.GetProperty(memberName)?.PropertyType
                     ?? declaringType.GetField(memberName)?.FieldType
                     ?? typeof(object);
        return memberName;
    }

    private static BsonMemberMap? MemberMap(Type type, string memberName) =>
        BsonClassMap.LookupClassMap(type).AllMemberMaps.FirstOrDefault(map => map.MemberName == memberName);
}
