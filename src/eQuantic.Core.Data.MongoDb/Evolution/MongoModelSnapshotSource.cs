using eQuantic.Core.Data.Evolution;
using MongoDB.Bson.Serialization;

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

        return new EntitySnapshot(entityType.FullName ?? entityType.Name,
            MongoModeling.CollectionName(entityType),
            classMap.AllMemberMaps
                .Select(member => Describe(entityType, member))
                .OrderBy(field => field.Member, StringComparer.Ordinal)
                .ToList())
        {
            Keys = classMap.IdMemberMap is { } id ? [id.MemberName] : [],
        };
    }

    private static FieldSnapshot Describe(Type entityType, BsonMemberMap member)
    {
        var stored = member.MemberType;
        return new FieldSnapshot(member.MemberName, member.ElementName,
            (Nullable.GetUnderlyingType(stored) ?? stored).FullName ?? stored.Name)
        {
            // A document may always be missing a field, whatever the member's CLR type says — that is the
            // condition a migration exists to fix, not one the model can rule out.
            Nullable = true,
            PreviousNames = MemberVocabulary.PreviousNames(member.MemberInfo),
            DefaultLiteral = MemberVocabulary.DefaultLiteral(member.MemberInfo),
        };
    }
}
