using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.Query;

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

    [Test]
    public void StoredAs_renames_flow_through_keys_columns_and_search()
    {
        var builder = new CassandraModelBuilder();
        builder.Entity<RenamedReading>(_ => { });
        var configuration = builder.Build().For(typeof(RenamedReading));

        Assert.That(configuration.PartitionKeys, Is.EqualTo(new[] { "sensor_id" }), "the partition key uses the stored name");
        Assert.That(configuration.ClusteringKeys.Single().Column, Is.EqualTo("at_ts"));
        Assert.That(configuration.KeyColumn, Is.EqualTo("sensor_id"));
        Assert.That(configuration.CanLike("quality_text", out _), Is.True, "the search index follows the rename");
        Assert.That(configuration.ColumnFor("SensorId"), Is.EqualTo("sensor_id"));
        Assert.That(configuration.MemberFor("at_ts"), Is.EqualTo("At"));
        Assert.That(configuration.Columns.Single(column => column.Name == "sensor_id").Member, Is.EqualTo("SensorId"));
    }

    [Test]
    public void Renderer_and_mapper_emit_stored_names_and_read_members()
    {
        var builder = new CassandraModelBuilder();
        builder.Entity<RenamedReading>(entity => entity.Column(x => x.Value, "v"));
        var configuration = builder.Build().For(typeof(RenamedReading));

        var (cql, values, _) = CassandraCqlRenderer.Render(configuration,
            FilterInterpreter.Interpret<RenamedReading>(x => x.SensorId == 5));
        Assert.That(cql, Is.EqualTo("sensor_id = ?"), "the filter names the member; the CQL names the column");
        Assert.That(values, Is.EqualTo(new object[] { 5 }));

        var (upsert, upsertValues) = CassandraMapper.BuildUpsert(configuration,
            new RenamedReading { SensorId = 7, Value = 42 });
        Assert.That(upsert, Does.Contain("sensor_id").And.Contain("v"), "the INSERT lists stored columns");
        Assert.That(upsertValues, Does.Contain(7).And.Contain(42), "values read from the CLR members");
    }

    [Test]
    public void Explain_reports_the_mapping_decisions()
    {
        var builder = new CassandraModelBuilder();
        builder.Entity<RenamedReading>(_ => { });
        var report = builder.Build().Explain();

        Assert.That(report, Does.Contain("table \"renamed_readings\""));
        Assert.That(report, Does.Contain("partition key: (sensor_id)"));
        Assert.That(report, Does.Contain("clustering keys: at_ts DESC"));
        Assert.That(report, Does.Contain("SensorId \"sensor_id\""), "the report shows member and stored name");
        Assert.That(report, Does.Contain("search index: \"quality_text\""));
    }
}

/// <summary>An entity whose stored names differ from its members — <c>[StoredAs]</c> renames everywhere.</summary>
[Entity("renamed_readings")]
public sealed class RenamedReading
{
    [PartitionKey]
    [StoredAs("sensor_id")]
    public int SensorId { get; set; }

    [ClusteringKey(Descending = true)]
    [StoredAs("at_ts")]
    public DateTime At { get; set; }

    [SearchIndex(Mode = SearchMode.Contains)]
    [StoredAs("quality_text")]
    public string Quality { get; set; } = "";

    public int Value { get; set; }
}
