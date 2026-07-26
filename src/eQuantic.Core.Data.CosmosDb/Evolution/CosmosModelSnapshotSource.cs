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

    private static EntitySnapshot Describe(CosmosEntityConfiguration configuration) =>
        new(configuration.EntityType.FullName ?? configuration.EntityType.Name,
            configuration.ContainerName,
            Members(configuration.EntityType)
                .Select(member => Describe(configuration.EntityType, member))
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

    private static FieldSnapshot Describe(Type entityType, PropertyInfo member)
    {
        var stored = member.PropertyType;
        return new FieldSnapshot(member.Name, CosmosNaming.StoredName(member),
            (Nullable.GetUnderlyingType(stored) ?? stored).FullName ?? stored.Name)
        {
            // As with any document store, a property may simply be absent from what is already written.
            Nullable = true,
            PreviousNames = MemberVocabulary.PreviousNames(member),
            DefaultLiteral = MemberVocabulary.DefaultLiteral(member),
        };
    }

    /// <summary>The properties the serializer will write: public, readable, and not explicitly unmapped.</summary>
    private static IEnumerable<PropertyInfo> Members(Type entityType) =>
        entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .Where(property => property.GetCustomAttribute<Modeling.UnmappedAttribute>(inherit: true) is null);

    /// <summary>The member a partition key path names — "/tenantId" describes the same thing as TenantId.</summary>
    private static string Member(string path) => path.TrimStart('/').Replace("/", ".");
}
