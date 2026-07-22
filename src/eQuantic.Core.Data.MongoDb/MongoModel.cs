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

    /// <summary>The entity types the model configured.</summary>
    public IReadOnlyCollection<Type> EntityTypes => _notes.Keys;

    internal void Add(Type entityType, List<string> notes) => _notes[entityType] = notes;

    /// <summary>
    ///     Describes every mapping decision — collection, id member, element renames, exclusions and value
    ///     conversions — read from the driver's class maps, the way <c>Explain()</c> describes a query. Read this
    ///     instead of guessing what an element ended up being called.
    /// </summary>
    public string Explain()
    {
        var report = new System.Text.StringBuilder();
        foreach (var (entityType, notes) in _notes.OrderBy(entry => entry.Key.Name))
        {
            report.AppendLine($"{entityType.Name} -> collection \"{MongoModeling.CollectionName(entityType)}\"");

            var classMap = BsonClassMap.LookupClassMap(entityType);
            if (classMap.IdMemberMap is { } id)
            {
                report.AppendLine($"  id: {id.MemberName} \"_id\"");
            }

            foreach (var member in classMap.AllMemberMaps.Where(member => member != classMap.IdMemberMap))
            {
                report.AppendLine($"  member: {member.MemberName} \"{member.ElementName}\"");
            }

            foreach (var note in notes)
            {
                report.AppendLine($"  {note}");
            }
        }

        return report.ToString();
    }
}

/// <summary>Fluent builder for the <see cref="MongoModel" /> — one <c>Entity</c> call per mapped type.</summary>
public sealed class MongoModelBuilder
{
    private readonly MongoModel _model = new();

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
