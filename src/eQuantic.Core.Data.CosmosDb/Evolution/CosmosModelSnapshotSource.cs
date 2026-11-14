using System.Reflection;
using eQuantic.Core.Data.Evolution;

namespace eQuantic.Core.Data.CosmosDb.Evolution;

/// <summary>
///     Describes the registered Cosmos DB mappings as a store-neutral snapshot.
///     <para>
///         Cosmos keeps rather less in its model than the other stores: the container, the partition key paths,
///         the clustering order and the time to live — but no list of properties, because a container has no
///         schema to list. So the fields come from the entity type itself, named the way the serializer will name
///         them, which is the same answer by a different route.
///     </para>
///     <para>
///         An enum the serializer writes as its labels (annotated <c>[EnumType]</c>, or declared with the model's
///         <c>Enum&lt;TEnum&gt;()</c>) is recorded as those labels, alone or as a collection's elements, and a member
///         the store assigns on insert with what it assigns: neither shows in the CLR type, and both change what the
///         documents hold.
///     </para>
///     <para>
///         The partition key is the part worth recording carefully. It is fixed for the life of a container, so a
///         comparison that sees it move has found something no migration can perform — better said plainly than
///         attempted.
///     </para>
/// </summary>
public sealed class CosmosModelSnapshotSource(CosmosModel model) : IModelSnapshotSource
{
    /// <inheritdoc />
    public string Provider => "cosmosdb";

    /// <inheritdoc />
    public ModelSnapshot Describe() => new(Provider, model.Configurations.Values
        .Select(Describe)
        .OrderBy(entity => entity.EntityType, StringComparer.Ordinal)
        .ToList());

    private EntitySnapshot Describe(CosmosEntityConfiguration configuration) =>
        new(configuration.EntityType.FullName ?? configuration.EntityType.Name,
            configuration.ContainerName,
            Members(configuration.EntityType)
                .Select(member => Describe(configuration, member))
                .OrderBy(field => field.Member, StringComparer.Ordinal)
                .ToList())
        {
            // Cosmos identifies a document by id within its partition; both together are the key.
            Keys = ["Id", .. configuration.PartitionKeyPaths.Select(Member)],
            PartitionKeys = configuration.PartitionKeyPaths.Select(Member).ToList(),
            Clustering = configuration.ClusteringPaths
                .Select(clustering => new ClusteringSnapshot(Member(clustering.Path), clustering.Descending))
                .ToList(),
            ConcurrencyField = configuration.HasConcurrencyToken ? "_etag" : null,
            TimeToLiveSeconds = configuration.DefaultTimeToLiveSeconds,
        };

    private FieldSnapshot Describe(CosmosEntityConfiguration configuration, PropertyInfo member)
    {
        var (storedType, labels) = StoredTypeOf(member.PropertyType);
        return new FieldSnapshot(member.Name, CosmosNaming.StoredName(member), storedType)
        {
            // As with any document store, a property may simply be absent from what is already written.
            Nullable = true,
            PreviousNames = MemberVocabulary.PreviousNames(member),
            DefaultLiteral = MemberVocabulary.DefaultLiteral(member),
            Generated = configuration.StoreGenerated.FirstOrDefault(candidate => candidate.Property.Name == member.Name)
                is { } generated
                ? StoredForm.Generated(generated.Value)
                : null,
            Labels = labels is null ? [] : LabelSnapshot.Of(labels),
        };
    }

    /// <summary>
    ///     The member's CLR type, or the labels of the enum the serializer writes as labels: its converter is the
    ///     type's, so it applies to the member alone (a nullable one unwrapped) and to a collection's elements.
    /// </summary>
    private (string StoredType, Type? Labels) StoredTypeOf(Type memberType)
    {
        var stored = Nullable.GetUnderlyingType(memberType) ?? memberType;
        if (IsLabelEnum(stored))
        {
            return (StoredForm.Labels(stored), stored);
        }

        if (stored != typeof(string) && ElementOf(stored) is { } element && IsLabelEnum(element))
        {
            return (StoredForm.LabelCollection(element), element);
        }

        return (stored.FullName ?? memberType.Name, null);
    }

    /// <summary>Whether the serializer writes the enum as its labels: declared on the model, or annotated.</summary>
    private bool IsLabelEnum(Type type) =>
        type.IsEnum && (model.LabelEnums.Contains(type) || Modeling.EnumTypeAttribute.For(type) is not null);

    /// <summary>The element type of an array or a sequence (nullable unwrapped), or <c>null</c>.</summary>
    private static Type? ElementOf(Type type)
    {
        var element = type.IsArray
            ? type.GetElementType()
            : type.GetInterfaces().Append(type)
                .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                ?.GetGenericArguments()[0];
        return element is null ? null : Nullable.GetUnderlyingType(element) ?? element;
    }

    /// <summary>The properties the serializer will write: public, readable, and not explicitly unmapped.</summary>
    private static IEnumerable<PropertyInfo> Members(Type entityType) =>
        entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .Where(property => property.GetCustomAttribute<Modeling.UnmappedAttribute>(inherit: true) is null);

    /// <summary>The member a partition key path names — "/tenantId" describes the same thing as TenantId.</summary>
    private static string Member(string path) => path.TrimStart('/').Replace("/", ".");
}
