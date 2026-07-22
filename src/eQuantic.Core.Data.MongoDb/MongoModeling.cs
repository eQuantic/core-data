using eQuantic.Core.Data.Modeling;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;

namespace eQuantic.Core.Data.MongoDb;

/// <summary>
///     Wires the store-neutral <c>eQuantic.Core.Data.Modeling</c> annotations into the driver's class maps —
///     <c>[EntityKey]</c> becomes the <c>_id</c> member, <c>[StoredAs]</c> becomes the BSON element name and
///     <c>[Unmapped]</c> removes the member — so an entity never needs <c>[BsonId]</c>, <c>[BsonElement]</c> or
///     any other driver attribute. Registered once by the DI extensions.
/// </summary>
internal static class MongoModeling
{
    private static bool _registered;
    private static readonly object Gate = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, string> CollectionOverrides = new();

    /// <summary>
    ///     The collection name for an entity type: the fluent model's <c>Collection(...)</c>, then
    ///     <c>[Entity("...")]</c>, then the type name. The overrides live process-wide, like the driver's own
    ///     class-map registry — the same lifecycle the rest of the mapping already has.
    /// </summary>
    public static string CollectionName(Type entityType) =>
        CollectionOverrides.TryGetValue(entityType, out var overridden)
            ? overridden
            : EntityAttribute.NameFor(entityType) ?? entityType.Name;

    /// <summary>Registers a fluent collection-name override (last declaration wins).</summary>
    public static void SetCollectionName(Type entityType, string name) => CollectionOverrides[entityType] = name;

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
                new ConventionPack { new StoredAsConvention(), new UnmappedConvention(), new EntityKeyConvention() }, _ => true);
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

    /// <summary>
    ///     <c>[EntityKey]</c> makes the member the document's <c>_id</c> — the driver's own convention only
    ///     recognizes a member literally named <c>Id</c>, so a differently-named key would otherwise persist as a
    ///     plain field with a duplicate ObjectId <c>_id</c>.
    /// </summary>
    private sealed class EntityKeyConvention : ConventionBase, IClassMapConvention
    {
        public void Apply(BsonClassMap classMap)
        {
            foreach (var member in classMap.DeclaredMemberMaps)
            {
                if (member.MemberInfo.GetCustomAttributes(typeof(EntityKeyAttribute), inherit: true).Length > 0)
                {
                    classMap.SetIdMember(member);
                    return;
                }
            }
        }
    }
}
