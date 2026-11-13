using eQuantic.Core.Data.Modeling;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;

namespace eQuantic.Core.Data.MongoDb;

/// <summary>A member the store assigns on insert: the property, what the store assigns, and its BSON element.</summary>
/// <param name="Property">The entity's property.</param>
/// <param name="Value">What the store assigns (resolved: <see cref="StoreValue.Sequence" /> or <see cref="StoreValue.Clock" />).</param>
/// <param name="ElementName">The stored element name.</param>
internal sealed record MongoGeneratedMember(System.Reflection.PropertyInfo Property, StoreValue Value, string ElementName)
{
    /// <summary>Sets the store's value on the entity, in the member's own type.</summary>
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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, List<(string Member, bool Descending)>> ClusteringOverrides = new();

    /// <summary>
    ///     The ordered-read declaration for an entity type: fluent <c>ClusteringKey(...)</c> calls, then the
    ///     <c>[ClusteringKey]</c> annotations in <c>Order</c>. <c>EnsureCollection()</c> materializes them as one
    ///     compound index with the declared directions.
    /// </summary>
    public static IReadOnlyList<(string Member, bool Descending)> ClusteringKeys(Type entityType)
    {
        if (ClusteringOverrides.TryGetValue(entityType, out var overridden))
        {
            return overridden;
        }

        return entityType
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(property => (Property: property,
                Annotation: property.GetCustomAttributes(typeof(ClusteringKeyAttribute), inherit: true)
                    is [ClusteringKeyAttribute annotation, ..] ? annotation : null))
            .Where(candidate => candidate.Annotation is not null)
            .OrderBy(candidate => candidate.Annotation!.Order)
            .Select(candidate => (candidate.Property.Name, candidate.Annotation!.Descending))
            .ToList();
    }

    /// <summary>Appends a fluent ordered-read declaration (fluent replaces the annotations wholesale).</summary>
    public static void AddClusteringKey(Type entityType, string member, bool descending) =>
        ClusteringOverrides.AddOrUpdate(entityType,
            _ => [(member, descending)],
            (_, existing) =>
            {
                existing.Add((member, descending));
                return existing;
            });

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Dictionary<string, StoreValue>> GeneratedOverrides = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, IReadOnlyList<MongoGeneratedMember>> GeneratedMembers = new();

    /// <summary>Registers a fluent store-generated declaration (it wins over the member's annotation).</summary>
    public static void SetStoreGenerated(Type entityType, string member, StoreValue value)
    {
        GeneratedOverrides.AddOrUpdate(entityType,
            _ => new Dictionary<string, StoreValue>(StringComparer.Ordinal) { [member] = value },
            (_, existing) =>
            {
                existing[member] = value;
                return existing;
            });
        GeneratedMembers.TryRemove(entityType, out _);
    }

    /// <summary>
    ///     The members the store assigns on insert (fluent <c>Generated(...)</c>, then <c>[StoreGenerated]</c>), with
    ///     what the store assigns resolved from each member's type. Throws, naming the member, when one cannot be
    ///     store-generated: a kind its type cannot take, the document's <c>_id</c>, the concurrency token, or a member
    ///     the class map does not store.
    /// </summary>
    public static IReadOnlyList<MongoGeneratedMember> StoreGenerated(Type entityType) =>
        GeneratedMembers.GetOrAdd(entityType, DescribeStoreGenerated);

    private static IReadOnlyList<MongoGeneratedMember> DescribeStoreGenerated(Type entityType)
    {
        var declared = new Dictionary<string, StoreValue>(StringComparer.Ordinal);
        foreach (var property in entityType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (property.GetCustomAttributes(typeof(StoreGeneratedAttribute), inherit: true) is [StoreGeneratedAttribute generated, ..])
            {
                declared[property.Name] = generated.Value;
            }
        }

        if (GeneratedOverrides.TryGetValue(entityType, out var overrides))
        {
            foreach (var (member, value) in overrides)
            {
                declared[member] = value;
            }
        }

        if (declared.Count == 0)
        {
            return [];
        }

        var classMap = BsonClassMap.LookupClassMap(entityType);
        var members = new List<MongoGeneratedMember>(declared.Count);
        foreach (var (member, value) in declared)
        {
            var property = entityType.GetProperty(member, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                           ?? throw new InvalidOperationException($"Entity '{entityType.Name}' has no member '{member}' to declare store-generated.");
            var resolved = StoreGeneratedAttribute.Resolve(value, property.PropertyType, $"{entityType.Name}.{member}");

            if (classMap.IdMemberMap?.MemberName == member || ConcurrencyMember(entityType)?.Name == member)
            {
                throw new InvalidOperationException(
                    $"'{entityType.Name}.{member}' is the document's _id or its concurrency token, and cannot also be declared store-generated.");
            }

            var memberMap = classMap.AllMemberMaps.FirstOrDefault(map => map.MemberName == member)
                            ?? throw new InvalidOperationException(
                                $"'{entityType.Name}.{member}' is declared store-generated, and the mapping does not store it.");
            if (!property.CanWrite)
            {
                throw new InvalidOperationException(
                    $"'{entityType.Name}.{member}' is declared store-generated, and has no setter to receive the store's value.");
            }

            members.Add(new MongoGeneratedMember(property, resolved, memberMap.ElementName));
        }

        return members;
    }

    /// <summary>Registers the annotation conventions and the stored-value serializers with the driver (idempotent).</summary>
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
            BsonSerializer.RegisterSerializationProvider(new MongoStoredValueSerializationProvider());
        }
    }

    /// <summary>
    ///     Declares <typeparamref name="TEnum" /> stored as its labels. The driver's serializers are global and fixed on
    ///     first use, so an enum the driver has already serialized as its number cannot change now: that refuses,
    ///     rather than leave the enum stored one way in some documents and another in the rest.
    /// </summary>
    public static void DeclareLabels<TEnum>() where TEnum : struct, Enum
    {
        Register();
        try
        {
            BsonSerializer.TryRegisterSerializer(new MongoEnumLabelSerializer<TEnum>());
        }
        catch (MongoDB.Bson.BsonSerializationException exception)
        {
            throw new InvalidOperationException(
                $"'{typeof(TEnum).Name}' was already serialized as its number in this process (the driver's serializers are " +
                $"global and fixed on first use), so it cannot be declared stored as its labels now. Declare Enum<{typeof(TEnum).Name}>() " +
                "before the first read or write, or annotate the enum with [EnumType].", exception);
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
