using eQuantic.Core.Data.Evolution;

namespace eQuantic.Core.Data.Cassandra.Evolution;

/// <summary>
///     Describes a <see cref="CassandraModel" /> as a store-neutral snapshot.
///     <para>
///         Cassandra is the one non-relational store with a real schema, and the only one where the snapshot can
///         record what a comparison most needs to refuse: the partition and clustering keys. A change to either
///         relocates every row that already exists, and there is no <c>ALTER</c> that does it — so recording them
///         is what lets the comparison say no by name instead of generating a statement the cluster rejects.
///     </para>
/// </summary>
public sealed class CassandraModelSnapshotSource(CassandraModel model) : IModelSnapshotSource
{
    /// <inheritdoc />
    public string Provider => "cassandra";

    /// <inheritdoc />
    public ModelSnapshot Describe() => new(Provider, model.Configurations.Values
        .Select(Describe)
        .OrderBy(entity => entity.EntityType, StringComparer.Ordinal)
        .ToList());

    private static EntitySnapshot Describe(CassandraEntityConfiguration configuration) =>
        new(configuration.EntityType.FullName ?? configuration.EntityType.Name,
            configuration.TableName,
            configuration.Columns
                .Select(column => Describe(configuration.EntityType, column))
                .OrderBy(field => field.Member, StringComparer.Ordinal)
                .ToList())
        {
            // The primary key is the partition key plus the clustering columns, in that order — that is what
            // identifies a row here, and comparing anything narrower would miss a key that moved.
            Keys = configuration.PartitionKeys
                .Concat(configuration.ClusteringKeys.Select(clustering => clustering.Column))
                .Select(configuration.MemberFor)
                .ToList(),
            PartitionKeys = configuration.PartitionKeys.Select(configuration.MemberFor).ToList(),
            Clustering = configuration.ClusteringKeys
                .Select(clustering => new ClusteringSnapshot(configuration.MemberFor(clustering.Column),
                    clustering.Descending))
                .ToList(),
            ConcurrencyField = configuration.ConcurrencyColumn is { } concurrency
                ? configuration.MemberFor(concurrency)
                : null,
            TimeToLiveSeconds = configuration.DefaultTtlSeconds,
            Search = configuration.SearchColumns
                .Select(search => new SearchSnapshot(configuration.MemberFor(search.Column), search.Mode.ToString()))
                .ToList(),
        };

    private static FieldSnapshot Describe(Type entityType, CassandraColumn column)
    {
        var member = MemberVocabulary.Find(entityType, column.Member);
        // The CQL type, not the CLR one: it is what the cluster holds, and a conversion between two CLR types
        // that land on the same CQL type is not a change to the store.
        return new FieldSnapshot(column.Member, column.Name, column.CqlType)
        {
            Nullable = true,
            PreviousNames = MemberVocabulary.PreviousNames(member),
            DefaultLiteral = MemberVocabulary.DefaultLiteral(member),
        };
    }
}
