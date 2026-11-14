using eQuantic.Core.Data.Cassandra.Evolution;
using eQuantic.Core.Data.Evolution;
using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>
///     What a Cassandra snapshot has to record for a later comparison to be worth anything: the keys, because
///     moving one is the change that cannot be made, and the vocabulary a member declares about itself.
/// </summary>
[TestFixture]
public sealed class CassandraModelSnapshotTests
{
    private sealed class Reading : IEntity<Guid>
    {
        public Guid Id { get; set; }

        [PreviousName("station")]
        public string Sensor { get; set; } = "";

        [DefaultValue(0.0)]
        public double Celsius { get; set; }

        public DateTime TakenAt { get; set; }

        public Guid GetKey() => Id;
        public void SetKey(Guid key) => Id = key;
    }

    private static EntitySnapshot Describe()
    {
        var builder = new CassandraModelBuilder();
        builder.Entity<Reading>(entity => entity
            .Table("readings")
            .PartitionKey(x => x.Sensor)
            .ClusteringKey(x => x.TakenAt, descending: true));

        return new CassandraModelSnapshotSource(builder.Build()).Describe().Entities.Single();
    }

    [Test]
    public void The_snapshot_names_the_table_and_the_whole_primary_key()
    {
        var entity = Describe();

        Assert.That(entity.Collection, Is.EqualTo("readings"));
        Assert.That(entity.PartitionKeys, Is.EqualTo(new[] { "Sensor" }));
        Assert.That(entity.Clustering.Single().Member, Is.EqualTo("TakenAt"));
        Assert.That(entity.Clustering.Single().Descending, Is.True);
        Assert.That(entity.Keys, Is.EqualTo(new[] { "Sensor", "TakenAt" }),
            "a row here is identified by the partition key and the clustering columns together");
    }

    [Test]
    public void The_snapshot_records_the_CQL_type_rather_than_the_CLR_one()
    {
        var celsius = Describe().Field("Celsius");

        Assert.That(celsius!.StoredType, Is.EqualTo("double"),
            "the cluster holds a CQL type; two CLR types landing on the same one are not a change to the store");
    }

    [Test]
    public void The_snapshot_carries_what_a_member_declares_about_itself()
    {
        var entity = Describe();

        Assert.That(entity.Field("Sensor")!.PreviousNames, Is.EqualTo(new[] { "station" }),
            "without this a rename reads as a drop and an add, and the values are lost");
        Assert.That(entity.Field("Celsius")!.DefaultLiteral, Is.EqualTo("0d"),
            "a declared default is what stops a generated change asking for one");
        Assert.That(entity.Field("TakenAt")!.DefaultLiteral, Is.Null);
    }

    // ---- enums stored as labels, and members the store assigns -------------------------------------

    /// <summary>An enum stored as its number unless the model declares it with <c>Enum&lt;TEnum&gt;()</c>.</summary>
    public enum Grade
    {
        Raw,
        Calibrated,
    }

    private sealed class Sample : IEntity<Guid>
    {
        public Guid Id { get; set; }

        public Grade Grade { get; set; }

        public Grade? Previous { get; set; }

        public long Number { get; set; }

        public DateTime TakenAt { get; set; }

        public Guid GetKey() => Id;
        public void SetKey(Guid key) => Id = key;
    }

    private static ModelSnapshot DescribeSamples(bool labels = false, bool generated = false)
    {
        var builder = new CassandraModelBuilder();
        if (labels)
        {
            builder.Enum<Grade>();
        }

        builder.Entity<Sample>(entity =>
        {
            entity.Table("samples").PartitionKey(x => x.Id);
            if (generated)
            {
                entity.Generated(x => x.Number).Generated(x => x.TakenAt);
            }
        });
        return new CassandraModelSnapshotSource(builder.Build()).Describe();
    }

    private static string Migration(ModelSnapshot before, ModelSnapshot after) =>
        MigrationWriter.Write(ModelDiffer.Compare(before, after), "Evolve", "Shop.Migrations",
            new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc));

    [Test]
    public void An_enum_is_recorded_as_its_CQL_number_or_as_its_labels()
    {
        Assert.That(DescribeSamples().Entities.Single().Field("Grade")!.StoredType, Is.EqualTo("int"),
            "an enum stored as its number is its underlying type's CQL");
        Assert.That(DescribeSamples(labels: true).Entities.Single().Field("Grade")!.StoredType,
            Is.EqualTo("labels:" + typeof(Grade).FullName),
            "text holding labels is not text holding anything else");
    }

    [Test]
    public void Switching_an_enum_to_its_labels_is_handed_back_instead_of_converted()
    {
        var difference = ModelDiffer.Compare(DescribeSamples(), DescribeSamples(labels: true));

        Assert.That(difference.Changes.Select(change => (change.Kind, change.Member, change.From)), Is.EquivalentTo(new[]
        {
            (ModelChangeKind.ConvertField, "Grade", "int"),
            (ModelChangeKind.ConvertField, "Previous", "int"),
        }));

        var migration = Migration(DescribeSamples(), DescribeSamples(labels: true));
        Assert.That(migration, Does.Not.Contain(".ConvertField("),
            "int to text would rewrite 1 as '1', not as its label");
        Assert.That(migration, Does.Contain("Cassandra cannot change a column's type"));
        Assert.That(migration, Does.Contain("rewrites each stored number as its label"));
    }

    [Test]
    public void A_store_generated_member_is_recorded_and_its_change_handed_back()
    {
        var after = DescribeSamples(generated: true);
        Assert.That(after.Entities.Single().Field("Number")!.Generated, Is.EqualTo("sequence"));
        Assert.That(after.Entities.Single().Field("TakenAt")!.Generated, Is.EqualTo("clock"));

        var migration = Migration(DescribeSamples(), after);
        Assert.That(migration, Does.Not.Contain("migration.For<"));
        Assert.That(migration, Does.Contain("\"equantic_sequences\" table"));
        Assert.That(migration, Does.Contain("toTimestamp(now())"));
    }
}
