using eQuantic.Core.Data.CosmosDb.Evolution;
using eQuantic.Core.Data.Evolution;
using eQuantic.Core.Data.Modeling;

namespace eQuantic.Core.Data.CosmosDb.Tests;

/// <summary>
///     A Cosmos snapshot records the container, the partition key it will be created with, and the properties the
///     serializer will write. The partition key is the part that earns its place: it is fixed for the container's
///     life, so a comparison that sees it move has found something no migration performs.
/// </summary>
[TestFixture]
public sealed class CosmosModelSnapshotTests
{
    private sealed class Ticket
    {
        public string Id { get; set; } = "";

        public string Tenant { get; set; } = "";

        [PreviousName("subject")]
        public string Title { get; set; } = "";

        [DefaultValue("open")]
        public string State { get; set; } = "";

        [Unmapped]
        public string Scratch { get; set; } = "";
    }

    private static EntitySnapshot Describe()
    {
        var builder = new CosmosModelBuilder();
        builder.Entity<Ticket>(entity => entity.Container("tickets").PartitionKey(x => x.Tenant));
        return new CosmosModelSnapshotSource(builder.Build()).Describe().Entities.Single();
    }

    [Test]
    public void The_snapshot_names_the_container_and_the_partition_key()
    {
        var entity = Describe();

        Assert.That(entity.Collection, Is.EqualTo("tickets"));
        Assert.That(entity.PartitionKeys, Is.EqualTo(new[] { "tenant" }));
        Assert.That(entity.Keys, Is.EqualTo(new[] { "Id", "tenant" }),
            "a document is identified by its id within its partition");
    }

    [Test]
    public void The_snapshot_uses_the_names_the_serializer_will_write()
    {
        Assert.That(Describe().Field("Title")!.Name, Is.EqualTo("title"),
            "the serializer camelCases, so that is the name a comparison has to reason about");
    }

    [Test]
    public void A_member_the_model_excludes_is_not_in_the_snapshot()
    {
        Assert.That(Describe().Field("Scratch"), Is.Null);
    }

    [Test]
    public void The_snapshot_carries_what_a_member_declares_about_itself()
    {
        var entity = Describe();

        Assert.That(entity.Field("Title")!.PreviousNames, Is.EqualTo(new[] { "subject" }));
        Assert.That(entity.Field("State")!.DefaultLiteral, Is.EqualTo("\"open\""));
    }
}
