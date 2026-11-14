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

    // ---- enums stored as labels, and members the store assigns -------------------------------------

    /// <summary>An enum stored as its number unless the model declares it with <c>Enum&lt;TEnum&gt;()</c>.</summary>
    public enum Stage
    {
        Open,
        Closed,
    }

    /// <summary>An enum stored as its labels wherever it is used, by the annotation on it.</summary>
    [EnumType]
    public enum Severity
    {
        Minor,
        Major,
    }

    private sealed class Incident
    {
        public string Id { get; set; } = "";

        public string Tenant { get; set; } = "";

        public Stage Stage { get; set; }

        public Stage? Previous { get; set; }

        public List<Stage> History { get; set; } = [];

        public Severity Severity { get; set; }

        public long Number { get; set; }

        public DateTime OpenedAt { get; set; }
    }

    private static ModelSnapshot DescribeIncidents(bool labels = false, bool generated = false)
    {
        var builder = new CosmosModelBuilder();
        if (labels)
        {
            builder.Enum<Stage>();
        }

        builder.Entity<Incident>(entity =>
        {
            entity.Container("incidents").PartitionKey(x => x.Tenant);
            if (generated)
            {
                entity.Generated(x => x.Number).Generated(x => x.OpenedAt);
            }
        });
        return new CosmosModelSnapshotSource(builder.Build()).Describe();
    }

    private static string Migration(ModelSnapshot before, ModelSnapshot after) =>
        MigrationWriter.Write(ModelDiffer.Compare(before, after), "Evolve", "Shop.Migrations",
            new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc));

    [Test]
    public void An_enum_is_recorded_as_its_number_or_as_its_labels()
    {
        var numbers = DescribeIncidents().Entities.Single();
        var labels = DescribeIncidents(labels: true).Entities.Single();

        Assert.That(numbers.Field("Stage")!.StoredType, Is.EqualTo(typeof(Stage).FullName));
        Assert.That(labels.Field("Stage")!.StoredType, Is.EqualTo("labels:" + typeof(Stage).FullName));
        Assert.That(labels.Field("Previous")!.StoredType, Is.EqualTo("labels:" + typeof(Stage).FullName));
        Assert.That(labels.Field("History")!.StoredType, Is.EqualTo("labels:" + typeof(Stage).FullName + "[]"),
            "the converter is the type's, so a list's elements are labels too");
        Assert.That(numbers.Field("Severity")!.StoredType, Is.EqualTo("labels:" + typeof(Severity).FullName),
            "[EnumType] on an enum the model never declares");
        Assert.That(labels.Field("Stage")!.Labels.Select(label => label.Label), Is.EqualTo(new[] { "Open", "Closed" }));
        Assert.That(labels.Field("History")!.Labels, Has.Count.EqualTo(2));
        Assert.That(numbers.Field("Stage")!.Labels, Is.Empty);
    }

    [Test]
    public void Switching_an_enum_to_its_labels_is_handed_back_instead_of_converted()
    {
        var difference = ModelDiffer.Compare(DescribeIncidents(), DescribeIncidents(labels: true));

        Assert.That(difference.Changes.Select(change => change.Kind), Is.All.EqualTo(ModelChangeKind.ConvertField));
        Assert.That(difference.Changes.Select(change => change.Member),
            Is.EquivalentTo(new[] { "History", "Previous", "Stage" }));

        var migration = Migration(DescribeIncidents(), DescribeIncidents(labels: true));
        Assert.That(migration, Does.Not.Contain(".ConvertField("));
        Assert.That(migration, Does.Contain("one read and one replace per document"));
    }

    [Test]
    public void A_store_generated_member_is_recorded_and_its_change_handed_back()
    {
        var after = DescribeIncidents(generated: true);
        Assert.That(after.Entities.Single().Field("Number")!.Generated, Is.EqualTo("sequence"));
        Assert.That(after.Entities.Single().Field("OpenedAt")!.Generated, Is.EqualTo("clock"));

        var difference = ModelDiffer.Compare(DescribeIncidents(), after);
        Assert.That(difference.Changes.Select(change => change.Kind), Is.All.EqualTo(ModelChangeKind.ChangeGeneration));

        var migration = Migration(DescribeIncidents(), after);
        Assert.That(migration, Does.Contain("\"_sequences\" container"));
        Assert.That(migration, Does.Contain("GetCurrentDateTime()"));
    }
}
