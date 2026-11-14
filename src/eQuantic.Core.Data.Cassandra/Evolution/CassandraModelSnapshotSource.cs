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
///     <para>
///         A column holding an enum's labels is recorded as those labels rather than as <c>text</c>, and a column the
///         store assigns on insert with what it assigns. An enum switched from its numbers to its labels moves its
///         column from an integral type to text, and recorded as <c>int</c> to <c>text</c> it would read as a type
///         conversion, which writes the number 1 as <c>"1"</c>, never as its label.
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
                .Select(column => Describe(configuration, column))
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

    private static FieldSnapshot Describe(CassandraEntityConfiguration configuration, CassandraColumn column)
    {
        var member = MemberVocabulary.Find(configuration.EntityType, column.Member);
        var clr = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;
        var labels = clr.IsEnum && column.CqlType == "text";
        // The CQL type, not the CLR one: it is what the cluster holds, and a conversion between two CLR types
        // that land on the same CQL type is not a change to the store. An enum's labels are the exception: text
        // holding labels is not text holding anything else.
        return new FieldSnapshot(column.Member, column.Name,
            labels ? StoredForm.Labels(clr) : column.CqlType)
        {
            Nullable = true,
            PreviousNames = MemberVocabulary.PreviousNames(member),
            DefaultLiteral = MemberVocabulary.DefaultLiteral(member),
            Generated = configuration.StoreGenerated
                .FirstOrDefault(generated => CassandraEntityConfiguration.Same(generated.Column, column.Name)) is { } store
                ? StoredForm.Generated(store.Value)
                : null,
            Labels = labels ? LabelSnapshot.Of(clr) : [],
        };
    }
}
