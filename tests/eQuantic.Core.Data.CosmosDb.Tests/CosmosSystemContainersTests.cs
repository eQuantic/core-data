using eQuantic.Core.Data.Repository;
using Microsoft.Azure.Cosmos;

namespace eQuantic.Core.Data.CosmosDb.Tests;

/// <summary>
///     The containers the provider keeps for itself (<c>_sequences</c>, <c>_locks</c>), against the Cosmos DB emulator:
///     created on first use, created again when they were removed after it.
/// </summary>
[TestFixture]
public sealed class CosmosSystemContainersTests : CosmosIntegrationTest
{
    [Test]
    public async Task The_counters_container_removed_after_its_first_use_is_created_again()
    {
        var repo = Resolve<IAsyncRepository<Receipt, string>>();
        var first = new Receipt { Category = Partition, Note = "first" };
        await repo.AddAsync(first);
        await Uow.CommitAsync();
        Assert.That(first.Number, Is.GreaterThan(0));

        // Removed behind the process's back, after the process ensured it.
        await Database.GetContainer(CosmosSequences.ContainerName).DeleteContainerAsync();

        var second = new Receipt { Category = Partition, Note = "second" };
        await repo.AddAsync(second);
        await Uow.CommitAsync();
        Assert.That(second.Number, Is.EqualTo(1), "the counter starts again in the container created again");
        var counter = await Database.GetContainer(CosmosSequences.ContainerName)
            .ReadItemAsync<System.Text.Json.Nodes.JsonNode>("products.number", new PartitionKey("products.number"));
        Assert.That(counter.Resource["value"]!.GetValue<long>(), Is.EqualTo(1L));
    }

    [Test]
    public void An_account_that_takes_writes_in_several_regions_is_refused_for_the_counters_and_the_leases()
    {
        Assert.That(CosmosSystemContainers.RefusalFor(["West US"], "A purpose."), Is.Null, "one write region");
        Assert.That(CosmosSystemContainers.RefusalFor([], "A purpose."), Is.Null);

        var refusal = CosmosSystemContainers.RefusalFor(["West US", "North Europe"], "A purpose.");
        Assert.That(refusal, Does.Contain("2 regions (West US, North Europe)").And.Contain("A purpose.").And.Contain("single write region"));
        Assert.That(CosmosNamedLocks.Purpose, Does.Contain("two holders"));

        // The account the SDK reads: an account with multi-region writes lists every write region as writable.
        var account = Newtonsoft.Json.JsonConvert.DeserializeObject<AccountProperties>(
            """{"writableLocations":[{"name":"West US","databaseAccountEndpoint":"https://a-westus/"},{"name":"North Europe","databaseAccountEndpoint":"https://a-northeurope/"}],"readableLocations":[]}""")!;
        Assert.That(CosmosSystemContainers.RefusalFor(account.WritableRegions.Select(region => region.Name).ToList(), "A purpose."), Is.Not.Null);
    }

    [Test]
    public async Task The_emulators_single_write_region_is_accepted()
    {
        var account = await Database.Client.ReadAccountAsync();
        Assert.That(account.WritableRegions.Count(), Is.EqualTo(1));

        var counters = await CosmosSystemContainers.GetAsync(Database, CosmosSequences.ContainerName, "A purpose.", CancellationToken.None);
        var leases = await CosmosNamedLocks.LeasesAsync(Database);
        Assert.That(counters.Id, Is.EqualTo(CosmosSequences.ContainerName));
        Assert.That(leases.Id, Is.EqualTo(CosmosNamedLocks.ContainerName));
        Assert.That((await leases.ReadContainerAsync()).Resource.PartitionKeyPath, Is.EqualTo("/id"));
    }
}
