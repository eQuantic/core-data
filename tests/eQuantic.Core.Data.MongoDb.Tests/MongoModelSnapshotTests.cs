using eQuantic.Core.Data.Evolution;
using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.MongoDb.Evolution;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>
///     A MongoDB snapshot describes the mapping the driver will actually use, read from its class maps rather than
///     from the type. That is the only truthful answer available: a collection has no schema, so what a comparison
///     can compare is what the application writes — element names, renames and exclusions included.
/// </summary>
[TestFixture]
public sealed class MongoModelSnapshotTests
{
    private sealed class Invoice
    {
        public string Id { get; set; } = "";

        [StoredAs("ref")]
        [PreviousName("code")]
        public string Reference { get; set; } = "";

        [DefaultValue("draft")]
        public string Status { get; set; } = "";

        [Unmapped]
        public string Scratch { get; set; } = "";
    }

    private static EntitySnapshot Describe()
    {
        var builder = new MongoModelBuilder();
        builder.Entity<Invoice>(_ => { });
        return new MongoModelSnapshotSource(builder.Build()).Describe().Entities.Single();
    }

    [Test]
    public void The_snapshot_uses_the_element_names_the_driver_will_write()
    {
        var reference = Describe().Field("Reference");

        Assert.That(reference!.Name, Is.EqualTo("ref"),
            "reflecting over the type instead would describe a shape nobody writes");
    }

    [Test]
    public void A_member_the_model_excludes_is_not_in_the_snapshot()
    {
        Assert.That(Describe().Field("Scratch"), Is.Null,
            "an unmapped member is not part of the collection's shape, so a comparison must not see it");
    }

    [Test]
    public void The_snapshot_carries_what_a_member_declares_about_itself()
    {
        var entity = Describe();

        Assert.That(entity.Field("Reference")!.PreviousNames, Is.EqualTo(new[] { "code" }));
        Assert.That(entity.Field("Status")!.DefaultLiteral, Is.EqualTo("\"draft\""),
            "this is what a document store needs most: the value the documents already there should hold");
    }

    [Test]
    public void Every_field_is_recorded_as_able_to_be_missing()
    {
        Assert.That(Describe().Fields.Select(field => field.Nullable), Is.All.True,
            "a document may always lack a field, whatever the member's CLR type says — that is the condition a "
            + "migration exists to fix, not one the model can rule out");
    }

    // ---- enums stored as labels, and members the store assigns -------------------------------------

    /// <summary>An enum stored as its number: nothing declares it otherwise.</summary>
    public enum Carrier
    {
        Post,
        Courier,
    }

    /// <summary>An enum stored as its labels wherever it is used, by the annotation on it.</summary>
    [EnumType]
    public enum Priority
    {
        Low,
        [StoredAs("urgent")]
        High,
    }

    /// <summary>An enum stored as its labels by the model's <c>Enum&lt;TEnum&gt;()</c>.</summary>
    public enum Packing
    {
        Box,
        Pallet,
    }

    private sealed class Shipment
    {
        public string Id { get; set; } = "";

        public Carrier Carrier { get; set; }

        public Priority Priority { get; set; }

        public Priority? Escalated { get; set; }

        public List<Priority> History { get; set; } = [];

        public Packing Packing { get; set; }

        [StoreGenerated]
        public long Number { get; set; }

        public DateTime StampedAt { get; set; }
    }

    private static ModelSnapshot DescribeShipments()
    {
        var builder = new MongoModelBuilder();
        builder.Enum<Packing>()
            .Entity<Shipment>(entity => entity.Generated(x => x.StampedAt));
        return new MongoModelSnapshotSource(builder.Build()).Describe();
    }

    /// <summary>
    ///     The snapshot as it was before <paramref name="member" /> took the form it has now. The driver fixes an enum's
    ///     serializer for the whole process on first use, so one process cannot describe the same enum stored both
    ///     ways; the earlier form is the one the source records for an enum stored as its number (see
    ///     <see cref="An_enum_is_recorded_as_its_labels_where_the_driver_writes_them" />), or no generation.
    /// </summary>
    private static ModelSnapshot Before(ModelSnapshot now, string member, Func<FieldSnapshot, FieldSnapshot> earlier)
    {
        var entity = now.Entities.Single();
        return now with
        {
            Entities = [entity with { Fields = entity.Fields.Select(field => field.Member == member ? earlier(field) : field).ToList() }],
        };
    }

    private static string Migration(ModelSnapshot before, ModelSnapshot after) =>
        MigrationWriter.Write(ModelDiffer.Compare(before, after), "Evolve", "Shop.Migrations",
            new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc));

    [Test]
    public void An_enum_is_recorded_as_its_labels_where_the_driver_writes_them()
    {
        var entity = DescribeShipments().Entities.Single();

        Assert.That(entity.Field("Carrier")!.StoredType, Is.EqualTo(typeof(Carrier).FullName), "stored as its number");
        Assert.That(entity.Field("Priority")!.StoredType, Is.EqualTo("labels:" + typeof(Priority).FullName),
            "[EnumType] on an enum the model never declares");
        Assert.That(entity.Field("Escalated")!.StoredType, Is.EqualTo("labels:" + typeof(Priority).FullName));
        Assert.That(entity.Field("History")!.StoredType, Is.EqualTo("labels:" + typeof(Priority).FullName + "[]"),
            "an array's elements go through the enum's serializer too");
        Assert.That(entity.Field("Packing")!.StoredType, Is.EqualTo("labels:" + typeof(Packing).FullName));
    }

    [Test]
    public void A_store_generated_member_is_recorded_with_what_the_store_assigns()
    {
        var entity = DescribeShipments().Entities.Single();

        Assert.That(entity.Field("Number")!.Generated, Is.EqualTo("sequence"), "[StoreGenerated] on the member");
        Assert.That(entity.Field("StampedAt")!.Generated, Is.EqualTo("clock"), "Generated(...) on the model");
        Assert.That(entity.Field("Carrier")!.Generated, Is.Null);
    }

    [Test]
    public void Switching_an_enum_to_its_labels_is_handed_back_instead_of_converted()
    {
        var after = DescribeShipments();
        var before = Before(after, "Priority", field => field with { StoredType = typeof(Priority).FullName! });

        var change = ModelDiffer.Compare(before, after).Changes.Single();
        Assert.That(change.Kind, Is.EqualTo(ModelChangeKind.ConvertField));

        var migration = Migration(before, after);
        Assert.That(migration, Does.Not.Contain(".ConvertField("),
            "a $convert would write the number 1 as the string '1', never as its label");
        Assert.That(migration, Does.Contain("rewrites each stored number as its label in every document"));
    }

    [Test]
    public void Making_a_member_store_generated_is_handed_back_with_where_its_counter_starts()
    {
        var after = DescribeShipments();
        var before = Before(after, "Number", field => field with { Generated = null });

        var change = ModelDiffer.Compare(before, after).Changes.Single();
        Assert.That(change.Kind, Is.EqualTo(ModelChangeKind.ChangeGeneration));

        var migration = Migration(before, after);
        Assert.That(migration, Does.Contain("\"_sequences\" collection"));
        Assert.That(migration, Does.Contain("sets that counter to the largest value already stored"));
    }
}
