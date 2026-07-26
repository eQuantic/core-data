using System.Text.Json.Nodes;
using eQuantic.Core.Data.Evolution;
using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.CosmosDb.Evolution;
using eQuantic.Core.Data.CosmosDb.Migration;
using Microsoft.Azure.Cosmos;

namespace eQuantic.Core.Data.CosmosDb.Tests;

/// <summary>
///     Evolving a Cosmos container: writing a newly mapped field into the documents that predate it, and checking
///     the containers against the model.
///     <para>
///         Both are shaped by the same fact — a container keeps no schema. So a backfill has to read and patch each
///         document that lacks the field, and a drift check can only answer about the things Cosmos does keep: the
///         container, and the partition key it was created with. It says so rather than sampling documents and
///         calling that a schema.
///     </para>
/// </summary>
[TestFixture]
public sealed class CosmosEvolutionTests : CosmosIntegrationTest
{
    private const string Container = "ledgers";

    private sealed class Ledger
    {
        public string Id { get; set; } = "";

        public string Tenant { get; set; } = "";

        [DefaultValue("web")]
        public string Channel { get; set; } = "";

        public string? Note { get; set; }
    }

    private static CosmosModel Model()
    {
        var builder = new CosmosModelBuilder();
        builder.Entity<Ledger>(entity => entity.Container(Container).PartitionKey(x => x.Tenant));
        return builder.Build();
    }

    private async Task ApplyAsync(Action<IMigrationBuilder> configure)
    {
        var builder = new MigrationBuilder();
        configure(builder);
        await new CosmosMigrationExecutor(Database, Model()).ApplyAsync(builder.Operations);
    }

    private async Task SeedAsync(params string[] ids)
    {
        var container = Database.GetContainer(Container);
        foreach (var id in ids)
        {
            var document = new JsonObject { ["id"] = id, ["tenant"] = "acme" };
            await container.CreateItemAsync(document, new PartitionKey("acme"));
        }
    }

    private async Task<List<JsonObject>> ReadAsync()
    {
        var results = new List<JsonObject>();
        using var iterator = Database.GetContainer(Container)
            .GetItemQueryIterator<JsonObject>(new QueryDefinition("SELECT * FROM c"));
        while (iterator.HasMoreResults)
        {
            results.AddRange(await iterator.ReadNextAsync());
        }

        return results.OrderBy(document => document["id"]!.GetValue<string>()).ToList();
    }

    [SetUp]
    public async Task FreshContainerAsync()
    {
        try
        {
            await Database.GetContainer(Container).DeleteContainerAsync();
        }
        catch (CosmosException failure) when (failure.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // nothing to clear
        }

        await ApplyAsync(migration => migration.For<Ledger>(ledger => ledger.EnsureCollection()));
    }

    [Test]
    public async Task A_declared_default_reaches_the_documents_that_predate_the_field()
    {
        await SeedAsync("a", "b");

        await ApplyAsync(migration => migration.For<Ledger>(ledger => ledger.AddField(x => x.Channel)));

        var documents = await ReadAsync();
        Assert.That(documents.Select(document => document["channel"]!.GetValue<string>()), Is.All.EqualTo("web"),
            "every document written before the member existed holds what the model says it holds");
    }

    [Test]
    public async Task A_member_that_declares_nothing_leaves_the_documents_alone()
    {
        await SeedAsync("a");

        await ApplyAsync(migration => migration.For<Ledger>(ledger => ledger.AddField(x => x.Note)));

        Assert.That((await ReadAsync()).Single().ContainsKey("note"), Is.False,
            "inventing a value would be worse than the absence: the absence is at least visible");
    }

    [Test]
    public async Task Documents_that_already_carry_the_field_keep_what_they_have()
    {
        var container = Database.GetContainer(Container);
        await container.CreateItemAsync(
            new JsonObject { ["id"] = "a", ["tenant"] = "acme", ["channel"] = "phone" }, new PartitionKey("acme"));
        await SeedAsync("b");

        await ApplyAsync(migration => migration.For<Ledger>(ledger => ledger.AddField(x => x.Channel)));

        var documents = await ReadAsync();
        Assert.That(documents[0]["channel"]!.GetValue<string>(), Is.EqualTo("phone"),
            "a backfill fills what is missing; it does not overwrite what is there");
        Assert.That(documents[1]["channel"]!.GetValue<string>(), Is.EqualTo("web"));
    }

    // ---- whole containers --------------------------------------------------------------------------

    [Test]
    public async Task Dropping_a_container_removes_it()
    {
        await ApplyAsync(migration => migration.For<Ledger>(ledger => ledger.DropCollection(Container)));

        var failure = Assert.ThrowsAsync<CosmosException>(() =>
            Database.GetContainer(Container).ReadContainerAsync());
        Assert.That(failure!.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.NotFound));
    }

    [Test]
    public void Renaming_a_container_is_refused_with_what_to_do_instead()
    {
        var failure = Assert.ThrowsAsync<NotSupportedException>(() =>
            ApplyAsync(migration => migration.For<Ledger>(ledger =>
                ledger.RenameCollection(Container, "ledgers_v2"))));

        Assert.That(failure!.Message, Does.Contain("fixed when it is created"));
        Assert.That(failure.Message, Does.Contain("copy the documents"),
            "a refusal that does not say what to do instead is just a wall");
    }

    [Test]
    public async Task Resizing_a_field_does_nothing_rather_than_failing()
    {
        await SeedAsync("a");

        await ApplyAsync(migration => migration.For<Ledger>(ledger => ledger.ResizeField(x => x.Channel)));

        Assert.That((await ReadAsync()).Single()["id"]!.GetValue<string>(), Is.EqualTo("a"),
            "a document's property is as big as its value; a migration written once for six stores must not throw");
    }

    // ---- drift -------------------------------------------------------------------------------------

    [Test]
    public async Task A_container_the_engine_created_reports_nothing()
    {
        var source = new CosmosDatabaseSnapshotSource(Model(), Database);

        var report = DriftComparer.Compare(source.Expect(), await source.ObserveAsync());

        Assert.That(report.Findings, Is.Empty,
            "the partition key the model declares must be the one the container was created with");
    }

    [Test]
    public async Task A_container_that_is_not_there_is_found()
    {
        await Database.GetContainer(Container).DeleteContainerAsync();
        var source = new CosmosDatabaseSnapshotSource(Model(), Database);

        var finding = DriftComparer.Compare(source.Expect(), await source.ObserveAsync()).Findings.Single();

        Assert.That(finding.Kind, Is.EqualTo(DriftKind.MissingCollection));
        Assert.That(finding.Breaks, Is.True, "Cosmos does not create a container on write; every request fails");
    }

    [Test]
    public async Task A_container_built_under_a_different_partition_key_is_named_as_unmigratable()
    {
        await Database.GetContainer(Container).DeleteContainerAsync();
        await Database.CreateContainerAsync(new ContainerProperties(Container, "/category"));

        var source = new CosmosDatabaseSnapshotSource(Model(), Database);
        var report = DriftComparer.Compare(source.Expect(), await source.ObserveAsync());

        var finding = report.Findings.Single(found => found.Kind == DriftKind.PartitionKeyDiffers);
        Assert.That(finding.Expected, Is.EqualTo("/tenant"));
        Assert.That(finding.Found, Is.EqualTo("/category"));
        Assert.That(report.NeedsRebuild, Is.True,
            "a partition key is fixed for the container's life — there is no migration, only a copy");
    }
}
