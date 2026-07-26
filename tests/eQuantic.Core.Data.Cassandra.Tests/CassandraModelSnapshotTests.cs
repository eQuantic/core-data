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
}
