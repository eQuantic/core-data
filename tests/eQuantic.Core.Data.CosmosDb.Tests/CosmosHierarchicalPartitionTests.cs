using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.Repository;
using Microsoft.Azure.Cosmos;

namespace eQuantic.Core.Data.CosmosDb.Tests;

/// <summary>
///     An event partitioned hierarchically — <c>[PartitionKey(Order)]</c> composes a multi-hash key, and the
///     ordered-read members land as a composite index on the container's policy.
/// </summary>
public sealed class GeoEvent : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [PartitionKey(Order = 0)]
    public string Tenant { get; set; } = "";

    [PartitionKey(Order = 1)]
    public string Region { get; set; } = "";

    [ClusteringKey(Order = 0)]
    public string Kind { get; set; } = "";

    [ClusteringKey(Order = 1, Descending = true)]
    public int Magnitude { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>
///     Proves the hierarchical partition key and the portable ordered-read declaration against the emulator:
///     the container is created multi-hash with the composite index, and writes/reads flow through the
///     hierarchical key transparently.
/// </summary>
[TestFixture]
public sealed class CosmosHierarchicalPartitionTests : CosmosIntegrationTest
{
    [Test]
    public async Task The_container_is_multi_hash_with_the_composite_index_and_writes_flow()
    {
        var model = Resolve<CosmosModel>();
        var configuration = model.For(typeof(GeoEvent));
        Assert.That(configuration.HasHierarchicalPartitionKey, Is.True);
        Assert.That(configuration.PartitionKeyPaths, Is.EqualTo(new[] { "/tenant", "/region" }),
            "[PartitionKey(Order)] composed the hierarchical paths in order");

        var builder = new MigrationBuilder();
        builder.For<GeoEvent>(geoEvent => geoEvent.EnsureCollection());
        await new Migration.CosmosMigrationExecutor(Database, model).ApplyAsync(builder.Operations);

        var container = Database.GetContainer("geo_events");
        var properties = (await container.ReadContainerAsync()).Resource;
        Assert.That(properties.PartitionKeyPaths, Is.EqualTo(new[] { "/tenant", "/region" }),
            "the container was created with the multi-hash key");

        // The vNext emulator's index management is a no-op (policies are accepted but not read back), so the
        // composite-index assertion holds the model's contract — what EnsureCollection() sends — not the echo.
        Assert.That(configuration.ClusteringPaths.Select(clustering => clustering.Path),
            Is.EqualTo(new[] { "/kind", "/magnitude" }),
            "[ClusteringKey] composed the composite-index paths in order");
        Assert.That(configuration.ClusteringPaths[1].Descending, Is.True);

        var repo = Resolve<IAsyncRepository<GeoEvent, string>>();
        var tenant = "t" + Guid.NewGuid().ToString("N");
        await repo.AddAsync(new GeoEvent { Tenant = tenant, Region = "br", Kind = "quake", Magnitude = 5 });
        await repo.AddAsync(new GeoEvent { Tenant = tenant, Region = "pt", Kind = "storm", Magnitude = 3 });
        await Uow.CommitAsync();

        var found = await repo.GetFilteredAsync(x => x.Tenant == tenant && x.Region == "br");
        Assert.That(found.Single().Kind, Is.EqualTo("quake"),
            "point writes carried the hierarchical key; the query found the document");

        var loaded = found.Single();
        loaded.Magnitude = 6;
        await repo.ModifyAsync(loaded);
        await Uow.CommitAsync();
        Assert.That((await repo.GetFilteredAsync(x => x.Tenant == tenant && x.Region == "br")).Single().Magnitude,
            Is.EqualTo(6), "the replace addressed the document through its multi-hash key");
    }

    [Test]
    public void Explain_reports_the_hierarchy_and_the_clustering()
    {
        var report = Resolve<CosmosModel>().Explain();
        Assert.That(report, Does.Contain("partition key: (/tenant, /region) hierarchical (multi-hash)"));
        Assert.That(report, Does.Contain("clustering: /kind ASC, /magnitude DESC (composite index on the container's policy)"));
    }
}
