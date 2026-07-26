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
}
