using System.Text.Json.Nodes;
using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.Repository;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.CosmosDb.Tests;

/// <summary>A graded gadget — the enum is stored as a lower-case string via the model's <c>Converts</c>.</summary>
public enum GadgetGrade
{
    Basic,
    Premium,
}

/// <summary>
///     An entity whose stored shape differs from its CLR shape: <c>[StoredAs]</c> renames, <c>[Unmapped]</c>
///     excludes, and the model converts the enum — no driver or JSON attribute anywhere.
/// </summary>
public sealed class RenamedGadget : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Category { get; set; } = "";

    [StoredAs("display_name")]
    public string Name { get; set; } = "";

    [Unmapped]
    public string Scratch { get; set; } = "";

    public GadgetGrade Grade { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>
///     Proves the <see cref="CosmosEntitySerializer" /> end to end against the emulator: the document stores the
///     renamed/converted shape, and — because the serializer extends <see cref="CosmosLinqSerializer" /> — the
///     SDK's LINQ translation queries that same shape. A rename can never desynchronize writes from queries.
/// </summary>
[TestFixture]
public sealed class CosmosSerializerTests : CosmosIntegrationTest
{
    private IAsyncRepository<RenamedGadget, string> Gadgets =>
        Resolve<IAsyncRepository<RenamedGadget, string>>();

    [Test]
    public async Task Renames_conversions_and_exclusions_hold_from_document_to_query()
    {
        var gadget = new RenamedGadget { Category = Partition, Name = "Elite", Scratch = "volatile", Grade = GadgetGrade.Premium };
        await Gadgets.AddAsync(gadget);
        await Uow.CommitAsync();

        // The raw document is the ground truth: stored names, converted values, and no excluded member.
        var container = Database.GetContainer(CosmosTestServer.ContainerName);
        var raw = (await container.ReadItemAsync<JsonNode>(gadget.Id, new PartitionKey(Partition))).Resource;
        Assert.That(raw["display_name"]?.GetValue<string>(), Is.EqualTo("Elite"), "[StoredAs] named the element");
        Assert.That(raw["name"], Is.Null, "the CLR name does not leak into the document");
        Assert.That(raw["grade"]?.GetValue<string>(), Is.EqualTo("premium"), "Converts stored the enum as a string");
        Assert.That(raw["scratch"], Is.Null, "[Unmapped] kept the member out of the document");

        // LINQ pushdown: the filter names CLR members; the SQL must name the stored elements.
        var byName = await Gadgets.GetFilteredAsync(x => x.Category == Partition && x.Name == "Elite");
        Assert.That(byName.Single().Name, Is.EqualTo("Elite"), "the renamed member filters against its stored name");

        var byGrade = await Gadgets.GetFilteredAsync(x => x.Category == Partition && x.Grade == GadgetGrade.Premium);
        Assert.That(byGrade.Single().Grade, Is.EqualTo(GadgetGrade.Premium),
            "the filter constant serialized through the converter (enum -> stored string)");
    }

    [Test]
    public async Task Sorting_and_projection_follow_the_stored_names()
    {
        await Gadgets.AddAsync(new RenamedGadget { Category = Partition, Name = "B", Grade = GadgetGrade.Basic });
        await Gadgets.AddAsync(new RenamedGadget { Category = Partition, Name = "A", Grade = GadgetGrade.Basic });
        await Uow.CommitAsync();

        var options = new eQuantic.Core.Data.Repository.Options.QueryOptions<RenamedGadget>()
            .Where(x => x.Category == Partition)
            .OrderBy(x => x.Name);
        var sorted = await Gadgets.GetAllAsync(options);
        Assert.That(sorted.Select(x => x.Name).ToArray(), Is.EqualTo(new[] { "A", "B" }),
            "ORDER BY renders the stored element name");

        var names = await Gadgets.GetMappedAsync(x => x.Name, options);
        Assert.That(names.ToArray(), Is.EqualTo(new[] { "A", "B" }), "the projection reads the stored element");
    }

    [Test]
    public void Filtering_on_an_unmapped_member_is_rejected_loudly()
    {
        var error = Assert.CatchAsync(async () =>
            await Gadgets.GetFilteredAsync(x => x.Category == Partition && x.Scratch == "x"))!;
        Assert.That(error.ToString(), Does.Contain("Unmapped"),
            "a member that does not exist in the document must refuse, never silently match nothing");
    }
}
