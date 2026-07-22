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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.PropertyInfo?> ConcurrencyMembers = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, (string Member, int Seconds)> TtlOverrides = new();

    /// <summary>
    ///     The optimistic-concurrency member for an entity type (fluent <c>ConcurrencyToken(...)</c>, then
    ///     <c>[ConcurrencyToken]</c>), or <c>null</c>. Writes on a token entity become conditional: the replace
    ///     filter carries the read version, the document carries the bump, and a commit whose replace matched
    ///     nothing throws <c>ConcurrencyConflictException</c>.
    /// </summary>
    public static System.Reflection.PropertyInfo? ConcurrencyMember(Type entityType) =>
        ConcurrencyMembers.GetOrAdd(entityType, type => type
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .FirstOrDefault(property =>
                property.GetCustomAttributes(typeof(ConcurrencyTokenAttribute), inherit: true).Length > 0));

    /// <summary>Registers a fluent concurrency-token override.</summary>
    public static void SetConcurrencyMember(Type entityType, System.Reflection.PropertyInfo member) =>
        ConcurrencyMembers[entityType] = member;

    /// <summary>
    ///     The TTL-index declaration for an entity type, or <c>null</c>. Resolution: the fluent
    ///     <c>TimeToLive(x =&gt; x.Member, span)</c>, then <c>[TimeToLive]</c> over the lifecycle
    ///     <c>CreatedAt</c> member (<c>IEntityTimeMark</c>). MongoDB expires <b>per document</b>, that long after
    ///     the indexed date — unlike Cosmos DB's container default; the semantic difference is the reason the
    ///     member is explicit here.
    /// </summary>
    public static (string Member, int Seconds)? TimeToLive(Type entityType)
    {
        if (TtlOverrides.TryGetValue(entityType, out var overridden))
        {
            return overridden;
        }

        if (System.Attribute.GetCustomAttribute(entityType, typeof(TimeToLiveAttribute)) is not TimeToLiveAttribute annotated)
        {
            return null;
        }

        if (!typeof(eQuantic.Core.Domain.Entities.IEntityTimeMark).IsAssignableFrom(entityType))
        {
            throw new InvalidOperationException(
                $"'{entityType.Name}' declares [TimeToLive] but MongoDB expires per document from a date member: " +
                "implement IEntityTimeMark (CreatedAt) so the TTL index has its date, or declare the member with " +
                "TimeToLive(x => x.Member, span) in the fluent model.");
        }

        return (nameof(eQuantic.Core.Domain.Entities.IEntityTimeMark.CreatedAt), annotated.Seconds);
    }

    /// <summary>Registers a fluent TTL declaration (member + lifetime).</summary>
    public static void SetTimeToLive(Type entityType, string member, int seconds) =>
        TtlOverrides[entityType] = (member, seconds);

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
