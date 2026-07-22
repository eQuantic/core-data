using eQuantic.Core.Data.Modeling;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;

namespace eQuantic.Core.Data.MongoDb;

/// <summary>
///     Wires the store-neutral <c>eQuantic.Core.Data.Modeling</c> annotations into the driver's class maps —
///     <c>[StoredAs]</c> becomes the BSON element name and <c>[Unmapped]</c> removes the member — so an entity
///     never needs <c>[BsonElement]</c> or any other driver attribute. Registered once by the DI extensions.
/// </summary>
internal static class MongoModeling
{
    private static bool _registered;
    private static readonly object Gate = new();

    /// <summary>The collection name for an entity type: <c>[Entity("...")]</c>, or the type name.</summary>
    public static string CollectionName(Type entityType) => EntityAttribute.NameFor(entityType) ?? entityType.Name;

    /// <summary>Registers the annotation conventions with the driver (idempotent).</summary>
    public static void Register()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            _registered = true;
            ConventionRegistry.Register("equantic-modeling",
                new ConventionPack { new StoredAsConvention(), new UnmappedConvention() }, _ => true);
        }
    }

    private sealed class StoredAsConvention : ConventionBase, IMemberMapConvention
    {
        public void Apply(BsonMemberMap memberMap)
        {
            if (memberMap.MemberInfo.GetCustomAttributes(typeof(StoredAsAttribute), inherit: true)
                    is [StoredAsAttribute stored, ..])
            {
                memberMap.SetElementName(stored.Name);
            }
        }
    }

    private sealed class UnmappedConvention : ConventionBase, IClassMapConvention
    {
        public void Apply(BsonClassMap classMap)
        {
            foreach (var member in classMap.DeclaredMemberMaps.ToList())
            {
                if (member.MemberInfo.GetCustomAttributes(typeof(UnmappedAttribute), inherit: true).Length > 0)
                {
                    classMap.UnmapMember(member.MemberInfo);
                }
            }
        }
    }
}
