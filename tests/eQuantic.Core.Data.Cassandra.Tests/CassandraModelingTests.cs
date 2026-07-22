using eQuantic.Core.Data.Modeling;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>An entity modeled entirely by the store-neutral annotations — access pattern included.</summary>
[Entity("annotated_readings")]
public sealed class AnnotatedReading
{
    [PartitionKey]
    public int SensorId { get; set; }

    [ClusteringKey(Descending = true)]
    public DateTime At { get; set; }

    [SearchIndex(Mode = SearchMode.Contains)]
    public string Quality { get; set; } = "";

    [Unmapped]
    public string Scratch { get; set; } = "";
}

/// <summary>
///     Pure model tests — no cluster. The eQuantic annotations declare the whole Cassandra access pattern:
///     table, partition and clustering keys, search index and exclusions; fluent overrides them when present.
/// </summary>
[TestFixture]
public sealed class CassandraModelingTests
{
    [Test]
    public void Annotations_declare_the_full_access_pattern()
    {
        var builder = new CassandraModelBuilder();
        builder.Entity<AnnotatedReading>(_ => { });
        var configuration = builder.Build().For(typeof(AnnotatedReading));

        Assert.That(configuration.TableName, Is.EqualTo("annotated_readings"));
        Assert.That(configuration.PartitionKeys, Is.EqualTo(new[] { "SensorId" }));
        Assert.That(configuration.ClusteringKeys.Single().Column, Is.EqualTo("At"));
        Assert.That(configuration.ClusteringKeys.Single().Descending, Is.True);
        Assert.That(configuration.CanLike("Quality", out var mode), Is.True, "[SearchIndex] declared the SASI column");
        Assert.That(mode, Is.EqualTo(CassandraSearchMode.Contains));
        Assert.That(configuration.Columns.Any(column => column.Name == "Scratch"), Is.False, "[Unmapped] excluded the member");
    }

    [Test]
    public void Fluent_overrides_the_annotations()
    {
        var builder = new CassandraModelBuilder();
        builder.Entity<AnnotatedReading>(entity => entity.Table("fluent_wins"));
        Assert.That(builder.Build().For(typeof(AnnotatedReading)).TableName, Is.EqualTo("fluent_wins"));
    }
}
