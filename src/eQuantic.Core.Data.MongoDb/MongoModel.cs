using System.Linq.Expressions;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace eQuantic.Core.Data.MongoDb;

/// <summary>
///     The registered MongoDB mappings: which entities the model configured, their collection names, and
///     <see cref="Explain" /> over the driver's actual class maps. The mappings themselves live in the driver's
///     global <see cref="BsonClassMap" /> registry (annotations via conventions, fluent via
///     <see cref="MongoModelBuilder" />) — this model is the introspection handle, not a parallel copy.
/// </summary>
public sealed class MongoModel
{
    private readonly Dictionary<Type, List<string>> _notes = new();
    private readonly HashSet<Type> _enums = [];

    /// <summary>The entity types the model configured.</summary>
    public IReadOnlyCollection<Type> EntityTypes => _notes.Keys;

    /// <summary>The enums the model declared stored as their labels (<c>Enum&lt;TEnum&gt;()</c>).</summary>
    public IReadOnlyCollection<Type> LabelEnums => _enums;

    internal void Add(Type entityType, List<string> notes) => _notes[entityType] = notes;

    internal void AddEnum(Type enumType) => _enums.Add(enumType);

    /// <summary>
    ///     Describes every mapping decision — collection, id member, element renames, exclusions and value
    ///     conversions, enums stored as their labels, JSON members and store-generated members — read from the
    ///     driver's class maps, the way <c>Explain()</c> describes a query. Read this instead of guessing what an
    ///     element ended up being called.
    /// </summary>
    public string Explain()
    {
        var report = new System.Text.StringBuilder();

        // The enums stored as labels: those the model declared, and the annotated ones its entities hold.
        var labelEnums = _enums
            .Concat(_notes.Keys
                .SelectMany(type => type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                .Select(property => Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType)
                .Where(type => type.IsEnum && Modeling.EnumTypeAttribute.For(type) is not null))
            .Distinct()
            .OrderBy(type => type.Name);
        foreach (var enumType in labelEnums)
        {
            report.AppendLine($"{enumType.Name} -> stored as labels, a BSON string (" +
                              string.Join(", ", Modeling.EnumLabels.Of(enumType).Select(label =>
                                  label.Key == label.Value ? label.Key : $"{label.Key} '{label.Value}'")) + ")");
        }

        foreach (var (entityType, notes) in _notes.OrderBy(entry => entry.Key.Name))
        {
            var collection = MongoModeling.CollectionName(entityType);
            report.AppendLine($"{entityType.Name} -> collection \"{collection}\"");

            var classMap = BsonClassMap.LookupClassMap(entityType);
            if (classMap.IdMemberMap is { } id)
            {
                report.AppendLine($"  id: {id.MemberName} \"_id\"");
            }

            var generated = MongoModeling.StoreGenerated(entityType);
            foreach (var member in classMap.AllMemberMaps.Where(member => member != classMap.IdMemberMap))
            {
                var stored = StoredValue(member.GetSerializer()) is { } kind ? $" ({kind})" : string.Empty;
                var store = generated.FirstOrDefault(candidate => candidate.Property.Name == member.MemberName) switch
                {
                    { Value: Modeling.StoreValue.Sequence } sequence =>
                        $" store-generated (sequence: $inc on \"{MongoSequences.CollectionName}\" document \"{MongoSequences.CounterId(collection, sequence.ElementName)}\")",
                    { Value: Modeling.StoreValue.Clock } => " store-generated (clock: the server's time, read once per commit)",
                    _ => string.Empty,
                };
                report.AppendLine($"  member: {member.MemberName} \"{member.ElementName}\"{stored}{store}");
            }

            foreach (var note in notes)
            {
                report.AppendLine($"  {note}");
            }
        }

        return report.ToString();
    }

    /// <summary>How a member's serializer stores it, when it is one of the stored-value kinds (nullable unwrapped).</summary>
    private static string? StoredValue(IBsonSerializer serializer) => serializer switch
    {
        IMongoStoredValueSerializer stored => stored.Describe(),
        IChildSerializerConfigurable { ChildSerializer: IMongoStoredValueSerializer child } => child.Describe(),
        _ => null,
    };
}

/// <summary>Fluent builder for the <see cref="MongoModel" /> — one <c>Entity</c> call per mapped type.</summary>
public sealed class MongoModelBuilder
{
    private readonly MongoModel _model = new();

    /// <summary>
    ///     Declares <typeparamref name="TEnum" /> stored as its labels, as <see cref="Modeling.EnumTypeAttribute" /> does
    ///     on the enum: each member is written, read, filtered and set-based-updated as its <c>[StoredAs]</c> label (or
    ///     its own name), a BSON string. An enum declared neither way is stored as its number. The driver's
    ///     serializers are global and fixed on first use, so declare it before the first read or write.
    /// </summary>
    /// <typeparam name="TEnum">The enum.</typeparam>
    /// <returns>The same builder for chaining.</returns>
    public MongoModelBuilder Enum<TEnum>() where TEnum : struct, System.Enum
    {
        MongoModeling.DeclareLabels<TEnum>();
        _model.AddEnum(typeof(TEnum));
        return this;
    }

    /// <summary>
    ///     Maps <typeparamref name="TEntity" /> — collection name, id member, element renames, exclusions and
    ///     value conversions. Applies once per process (the driver's class maps are global and freeze on first
    ///     use), with the usual precedence: conventions &lt; annotations &lt; fluent.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="configure">The fluent configuration.</param>
    /// <returns>The same builder for chaining.</returns>
    public MongoModelBuilder Entity<TEntity>(Action<MongoEntityBuilder<TEntity>> configure) where TEntity : class
    {
        MongoModeling.Register();

        var builder = new MongoEntityBuilder<TEntity>();
        configure(builder);

        if (builder.CollectionName is { } collection)
        {
            MongoModeling.SetCollectionName(typeof(TEntity), collection);
        }

        // AutoMap runs the conventions (the annotation pack included); the fluent steps override after it.
        BsonClassMap.TryRegisterClassMap<TEntity>(classMap =>
        {
            classMap.AutoMap();
            foreach (var step in builder.Steps)
            {
                step(classMap);
            }
        });

        foreach (var (member, value) in builder.GeneratedMembers)
        {
            MongoModeling.SetStoreGenerated(typeof(TEntity), member, value);
        }

        // Resolved now, so a member that cannot be store-generated refuses at model build, by name.
        _ = MongoModeling.StoreGenerated(typeof(TEntity));

        _model.Add(typeof(TEntity), builder.Notes);
        return this;
    }

    /// <summary>
    ///     Builds the model. The DI extensions call this for you; call it directly when hosting without DI — the
    ///     built model is the introspection handle (<see cref="MongoModel.Explain" />).
    /// </summary>
    public MongoModel Build() => _model;
}

/// <summary>Fluent configuration for one entity's MongoDB mapping.</summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public sealed class MongoEntityBuilder<TEntity> where TEntity : class
{
    internal string? CollectionName { get; private set; }
    internal List<Action<BsonClassMap<TEntity>>> Steps { get; } = [];
    internal List<string> Notes { get; } = [];
    internal List<(string Member, Modeling.StoreValue Value)> GeneratedMembers { get; } = [];

    internal MongoEntityBuilder()
    {
    }

    /// <summary>Sets the collection name (defaults to <c>[Entity("...")]</c>, then the type name).</summary>
    /// <param name="name">The collection name.</param>
    public MongoEntityBuilder<TEntity> Collection(string name)
    {
        CollectionName = name;
        return this;
    }

    /// <summary>Sets the member's BSON element name when the convention (the member name) does not fit.</summary>
    /// <typeparam name="TMember">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="elementName">The stored element name.</param>
    public MongoEntityBuilder<TEntity> Field<TMember>(Expression<Func<TEntity, TMember>> selector, string elementName)
    {
        Steps.Add(classMap => classMap.MapMember(selector).SetElementName(elementName));
        return this;
    }

    /// <summary>Excludes the member from the mapping (it neither persists nor reads back).</summary>
    /// <typeparam name="TMember">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    public MongoEntityBuilder<TEntity> Ignore<TMember>(Expression<Func<TEntity, TMember>> selector)
    {
        Steps.Add(classMap => classMap.UnmapMember(selector));
        return this;
    }

    /// <summary>Declares the member the document's <c>_id</c> (defaults to <c>[EntityKey]</c>, then <c>Id</c>).</summary>
    /// <typeparam name="TMember">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    public MongoEntityBuilder<TEntity> Key<TMember>(Expression<Func<TEntity, TMember>> selector)
    {
        Steps.Add(classMap => classMap.MapIdMember(selector));
        return this;
    }

    /// <summary>
    ///     Declares the optimistic-concurrency member: an integral version the entity carries. A staged replace
    ///     filters on the read version and writes the bump; a commit whose replace matched nothing throws
    ///     <c>ConcurrencyConflictException</c> — another writer changed (or removed) the document since it was read.
    ///     Entities without a token keep plain last-write-wins replaces.
    /// </summary>
    /// <typeparam name="TMember">The version member type (an integral type).</typeparam>
    /// <param name="selector">The member selector (e.g. <c>x =&gt; x.Version</c>).</param>
    public MongoEntityBuilder<TEntity> ConcurrencyToken<TMember>(Expression<Func<TEntity, TMember>> selector)
    {
        var member = typeof(TEntity).GetProperty(eQuantic.Linq.Expressions.MemberPathExtensions.GetMemberName(selector))
                     ?? throw new InvalidOperationException(
                         $"'{typeof(TEntity).Name}' has no property behind the concurrency-token selector.");
        MongoModeling.SetConcurrencyMember(typeof(TEntity), member);
        Notes.Add($"concurrency token: {member.Name} (replaces filter on the read version and bump it)");
        return this;
    }

    /// <summary>
    ///     Declares an ordered-read member — materialized as one compound index with the declared directions by
    ///     the migration's <c>EnsureCollection()</c>. Call in order for several.
    /// </summary>
    /// <typeparam name="TMember">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="descending">Whether the declared order is descending.</param>
    public MongoEntityBuilder<TEntity> ClusteringKey<TMember>(Expression<Func<TEntity, TMember>> selector, bool descending = false)
    {
        var member = eQuantic.Linq.Expressions.MemberPathExtensions.GetMemberName(selector);
        MongoModeling.AddClusteringKey(typeof(TEntity), member, descending);
        Notes.Add($"clustering: {member} {(descending ? "DESC" : "ASC")} (compound index)");
        return this;
    }

    /// <summary>
    ///     Declares the TTL index: each document expires <paramref name="timeToLive" /> after the date its
    ///     <paramref name="selector" /> member carries (MongoDB's per-document expiry — unlike Cosmos DB's
    ///     container default). The migration's <c>EnsureCollection()</c> creates the index.
    /// </summary>
    /// <typeparam name="TMember">The date member type.</typeparam>
    /// <param name="selector">The date member the expiry counts from (e.g. <c>x =&gt; x.CreatedAt</c>).</param>
    /// <param name="timeToLive">The lifetime after that date.</param>
    public MongoEntityBuilder<TEntity> TimeToLive<TMember>(Expression<Func<TEntity, TMember>> selector, TimeSpan timeToLive)
    {
        var member = eQuantic.Linq.Expressions.MemberPathExtensions.GetMemberName(selector);
        MongoModeling.SetTimeToLive(typeof(TEntity), member, (int)timeToLive.TotalSeconds);
        Notes.Add($"TTL: documents expire {(int)timeToLive.TotalSeconds}s after {member} (per-document TTL index)");
        return this;
    }

    /// <summary>
    ///     Declares a member whose value the store assigns on insert, as <see cref="Modeling.StoreGeneratedAttribute" />
    ///     does: the next number of an atomic counter for an integral member (<c>findOneAndUpdate</c> with <c>$inc</c> on
    ///     the <c>_sequences</c> collection, inside the open transaction), the server's clock for a date (read once per
    ///     commit and given to every insert of it), or the kind given. The value is on the entity when the commit
    ///     returns, and an update (a staged replace) keeps the stored value instead of writing the entity's.
    /// </summary>
    /// <typeparam name="TMember">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="value">What the store assigns; inferred from the member's type by default.</param>
    public MongoEntityBuilder<TEntity> Generated<TMember>(Expression<Func<TEntity, TMember>> selector,
        Modeling.StoreValue value = Modeling.StoreValue.Inferred)
    {
        GeneratedMembers.Add((eQuantic.Linq.Expressions.MemberPathExtensions.GetMemberName(selector), value));
        return this;
    }

    /// <summary>
    ///     Declares a value conversion for the member: documents store <typeparamref name="TStored" />, the entity
    ///     keeps <typeparamref name="TMember" />. Filters, sorts and set-based updates on the member render against
    ///     the stored representation — the driver serializes constants through the member's serializer.
    /// </summary>
    /// <typeparam name="TMember">The member's CLR type.</typeparam>
    /// <typeparam name="TStored">The stored (BSON) type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="toStored">Converts the CLR value to its stored representation.</param>
    /// <param name="fromStored">Converts the stored representation back.</param>
    public MongoEntityBuilder<TEntity> Converts<TMember, TStored>(Expression<Func<TEntity, TMember>> selector,
        Func<TMember, TStored> toStored, Func<TStored, TMember> fromStored)
    {
        Steps.Add(classMap => classMap.MapMember(selector)
            .SetSerializer(new MongoValueSerializer<TMember, TStored>(toStored, fromStored)));
        Notes.Add($"converts: {eQuantic.Linq.Expressions.MemberPathExtensions.GetMemberPath(selector)} " +
                  $"(stored as {typeof(TStored).Name})");
        return this;
    }
}

/// <summary>Serializes a member through a to/from pair over the stored type's own serializer.</summary>
/// <typeparam name="TMember">The member's CLR type.</typeparam>
/// <typeparam name="TStored">The stored (BSON) type.</typeparam>
internal sealed class MongoValueSerializer<TMember, TStored> : SerializerBase<TMember>
{
    private readonly IBsonSerializer<TStored> _stored = BsonSerializer.LookupSerializer<TStored>();
    private readonly Func<TMember, TStored> _toStored;
    private readonly Func<TStored, TMember> _fromStored;

    public MongoValueSerializer(Func<TMember, TStored> toStored, Func<TStored, TMember> fromStored)
    {
        _toStored = toStored;
        _fromStored = fromStored;
    }

    public override TMember Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args) =>
        _fromStored(_stored.Deserialize(context, new BsonDeserializationArgs()));

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, TMember value) =>
        _stored.Serialize(context, new BsonSerializationArgs(), _toStored(value));
}
