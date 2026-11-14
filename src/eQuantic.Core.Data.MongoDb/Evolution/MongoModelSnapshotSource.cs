using eQuantic.Core.Data.Evolution;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace eQuantic.Core.Data.MongoDb.Evolution;

/// <summary>
///     Describes the registered MongoDB mappings as a store-neutral snapshot, read from the driver's own class
///     maps rather than from a parallel copy of them.
///     <para>
///         That choice matters more here than anywhere else. A document store has no schema to consult, so the
///         only truthful answer to "what is this collection's shape" is the mapping the driver will actually use
///         when it serializes — element names included, renames and exclusions applied. Reflecting over the type
///         instead would describe a shape nobody writes.
///     </para>
///     <para>
///         The member's serializer also says how its values are stored: an enum the driver writes as its labels (an
///         <c>[EnumType]</c> enum, or one the model declared with <c>Enum&lt;TEnum&gt;()</c>) is recorded as those
///         labels, alone or as an array's elements, so switching it from its numbers reads as the change to every
///         stored document it is. A member the store assigns on insert is recorded with what it assigns.
///     </para>
///     <para>
///         What this cannot describe is the collection as it <em>is</em>: documents written before a member existed
///         simply lack it, and no amount of reading the model reveals that. That is the gap a declared default
///         closes, and why generating a change without one stops and asks.
///     </para>
/// </summary>
public sealed class MongoModelSnapshotSource(MongoModel model) : IModelSnapshotSource
{
    /// <inheritdoc />
    public string Provider => "mongodb";

    /// <inheritdoc />
    public ModelSnapshot Describe() => new(Provider, model.EntityTypes
        .Select(Describe)
        .OrderBy(entity => entity.EntityType, StringComparer.Ordinal)
        .ToList());

    private static EntitySnapshot Describe(Type entityType)
    {
        var classMap = BsonClassMap.LookupClassMap(entityType);
        var generated = MongoModeling.StoreGenerated(entityType);

        return new EntitySnapshot(entityType.FullName ?? entityType.Name,
            MongoModeling.CollectionName(entityType),
            classMap.AllMemberMaps
                .Select(member => Describe(member, generated))
                .OrderBy(field => field.Member, StringComparer.Ordinal)
                .ToList())
        {
            Keys = classMap.IdMemberMap is { } id ? [id.MemberName] : [],
        };
    }

    private static FieldSnapshot Describe(BsonMemberMap member, IReadOnlyList<MongoGeneratedMember> generated)
    {
        return new FieldSnapshot(member.MemberName, member.ElementName, StoredTypeOf(member))
        {
            // A document may always be missing a field, whatever the member's CLR type says — that is the
            // condition a migration exists to fix, not one the model can rule out.
            Nullable = true,
            PreviousNames = MemberVocabulary.PreviousNames(member.MemberInfo),
            DefaultLiteral = MemberVocabulary.DefaultLiteral(member.MemberInfo),
            Generated = generated.FirstOrDefault(candidate => candidate.Property.Name == member.MemberName) is { } store
                ? StoredForm.Generated(store.Value)
                : null,
        };
    }

    /// <summary>
    ///     The member's CLR type, or the labels of the enum its serializer writes as labels: the member's own (a
    ///     nullable one unwrapped), or its array's elements.
    /// </summary>
    private static string StoredTypeOf(BsonMemberMap member)
    {
        var serializer = member.GetSerializer();

        // An array's serializer also hands out its items' as a child, so it is asked first, for its elements.
        if (serializer is IBsonArraySerializer array)
        {
            if (array.TryGetItemSerializationInfo(out var item) && LabelEnumOf(item.Serializer, item.NominalType) is { } element)
            {
                return StoredForm.LabelCollection(element);
            }
        }
        else if (LabelEnumOf(serializer, member.MemberType) is { } single)
        {
            return StoredForm.Labels(single);
        }

        var stored = member.MemberType;
        return (Nullable.GetUnderlyingType(stored) ?? stored).FullName ?? stored.Name;
    }

    /// <summary>The enum a serializer of <paramref name="valueType" /> writes as its labels, or <c>null</c>.</summary>
    private static Type? LabelEnumOf(IBsonSerializer serializer, Type valueType) => serializer switch
    {
        IMongoEnumLabelSerializer labels => labels.EnumType,
        // A nullable enum's serializer wraps the enum's own (a dictionary's wraps its values', which is not this).
        IChildSerializerConfigurable { ChildSerializer: IMongoEnumLabelSerializer labels }
            when Nullable.GetUnderlyingType(valueType) is not null => labels.EnumType,
        _ => null,
    };
}
