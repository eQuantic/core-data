using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Read;

namespace eQuantic.Core.Data.CosmosDb.Tests;

/// <summary>
///     Proves the native Cosmos provider end to end against a real emulator. Focused (not per-method exhaustive)
///     and share-a-container: each test isolates its data under a unique partition value (<see cref="Partition" />).
/// </summary>
[TestFixture]
public sealed class CosmosProviderTests : CosmosIntegrationTest
{
    private QueryOptions<CosmosProduct> InPartition => new QueryOptions<CosmosProduct>().Where(p => p.Category == Partition);

    [Test]
    public async Task Add_and_commit_then_get_returns_the_entity()
    {
        var product = CosmosProduct.New("Keyboard", Partition, 10, 49.90m);

        await Repo.AddAsync(product);
        var affected = await Uow.CommitAsync();

        Assert.That(affected, Is.EqualTo(1));
        var found = await Repo.GetAsync(product.Id);
        Assert.That(found, Is.Not.Null);
        Assert.That(found!.Name, Is.EqualTo("Keyboard"));
    }

    [Test]
    public async Task Modify_and_commit_updates_the_entity()
    {
        var product = CosmosProduct.New("Mouse", Partition, 5, 19.90m);
        await Seed(product);

        product.Quantity = 8;
        await Repo.ModifyAsync(product);
        await Uow.CommitAsync();

        Assert.That((await Repo.GetAsync(product.Id))!.Quantity, Is.EqualTo(8));
    }

    [Test]
    public async Task Remove_and_commit_deletes_the_entity()
    {
        var product = CosmosProduct.New("Cable", Partition, 1, 1m);
        await Seed(product);

        await Repo.RemoveAsync(product);
        await Uow.CommitAsync();

        Assert.That(await Repo.GetAsync(product.Id), Is.Null);
    }

    [Test]
    public async Task Query_options_filter_sort_and_page()
    {
        await Seed(
            CosmosProduct.New("A", Partition, 1, 30m),
            CosmosProduct.New("B", Partition, 2, 10m),
            CosmosProduct.New("C", Partition, 3, 20m),
            CosmosProduct.New("D", Partition + "f", 4, 5m));

        var options = new QueryOptions<CosmosProduct>().Where(p => p.Category == Partition).OrderBy(p => p.Price);
        var page = await Repo.GetPagedAsync(new PageRequest(1, 2), options);

        Assert.That(page.TotalCount, Is.EqualTo(3));
        Assert.That(page.Items.Select(p => p.Name).ToArray(), Is.EqualTo(new[] { "B", "C" }));
    }

    [Test]
    public async Task Count_any_and_sum_honour_the_filter()
    {
        await Seed(
            CosmosProduct.New("A", Partition, 2, 30m),
            CosmosProduct.New("B", Partition, 3, 10m),
            CosmosProduct.New("C", Partition + "f", 4, 5m));

        Assert.That(await Repo.CountAsync(InPartition), Is.EqualTo(2));
        Assert.That(await Repo.AnyAsync(InPartition), Is.True);
        Assert.That(await Repo.SumAsync(p => p.Quantity, InPartition), Is.EqualTo(5));
    }

    [Test]
    public async Task Min_max_and_average_push_down_as_value_aggregates()
    {
        await Seed(
            CosmosProduct.New("A", Partition, 1, 10m),
            CosmosProduct.New("B", Partition, 3, 40m),
            CosmosProduct.New("C", Partition + "f", 9, 99m));

        var aggregates = (IAggregateReadRepository<CosmosProduct>)Repo;
        Assert.That(await aggregates.MinAsync(p => p.Price, InPartition), Is.EqualTo(10m));
        Assert.That(await aggregates.MaxAsync(p => p.Price, InPartition), Is.EqualTo(40m));
        Assert.That(await aggregates.AverageAsync(p => p.Quantity, InPartition), Is.EqualTo(2d),
            "the partition-pinning filter scopes the aggregate to one partition");
    }

    [Test]
    public async Task Typed_group_by_runs_as_a_server_side_group()
    {
        await Seed(
            CosmosProduct.New("A", Partition, 1, 10m),
            CosmosProduct.New("B", Partition, 3, 20m),
            CosmosProduct.New("C", Partition + "f", 4, 5m));

        var grouped = (IGroupedReadRepository<CosmosProduct>)Repo;
        var groups = await grouped.GroupByAsync(x => x.Category,
            g => new { Category = g.Key, Items = g.Count(), Value = g.Sum(x => x.Price) },
            options: InPartition);

        Assert.That(groups.Single(), Is.EqualTo(new { Category = Partition, Items = 2, Value = 30m }),
            "the SDK translated the rebuilt (key, values) projection to a GROUP BY");

        Assert.That(async () => await grouped.GroupByAsync(x => x.Category, g => new { g.Key },
                having: g => g.Count() > 1),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("HAVING"),
            "Cosmos SQL has no HAVING and the contract says so");
    }

    [Test]
    public async Task DeleteMany_removes_matching_documents()
    {
        await Seed(
            CosmosProduct.New("A", Partition, 1, 1m),
            CosmosProduct.New("B", Partition, 1, 1m),
            CosmosProduct.New("C", Partition + "f", 1, 1m));

        var deleted = await Repo.DeleteManyAsync(p => p.Category == Partition);

        Assert.That(deleted, Is.EqualTo(2));
        var food = new QueryOptions<CosmosProduct>().Where(p => p.Category == Partition + "f");
        Assert.That(await Repo.CountAsync(food), Is.EqualTo(1));
    }

    [Test]
    public async Task UpdateMany_patches_matching_documents()
    {
        var name = Partition + "-A";
        await Seed(
            CosmosProduct.New(name, Partition, 1, 1m),
            CosmosProduct.New("B", Partition, 1, 1m));

        var updated = await Repo.UpdateManyAsync(p => p.Name == name, _ => new CosmosProduct { Quantity = 99 });

        Assert.That(updated, Is.EqualTo(1));
        var byName = new QueryOptions<CosmosProduct>().Where(p => p.Name == name);
        Assert.That((await Repo.GetSingleAsync(byName))!.Quantity, Is.EqualTo(99));
    }

    [Test]
    public async Task Transaction_commit_persists_the_single_partition_batch()
    {
        await Uow.BeginTransactionAsync();
        await Repo.AddAsync(CosmosProduct.New("Tx1", Partition, 1, 1m));
        await Repo.AddAsync(CosmosProduct.New("Tx2", Partition, 1, 1m));
        await Uow.CommitAsync();
        await Uow.CommitTransactionAsync();

        Assert.That(await Repo.CountAsync(InPartition), Is.EqualTo(2));
    }

    [Test]
    public async Task Migration_runner_applies_once_and_declares_the_composite_index()
    {
        Assert.That(await Runner.RunAsync(), Is.GreaterThanOrEqualTo(1));
        Assert.That(await Runner.RunAsync(), Is.EqualTo(0));

        var properties = (await Database.GetContainer(CosmosTestServer.ContainerName).ReadContainerAsync()).Resource;
        Assert.That(properties.IndexingPolicy.CompositeIndexes, Is.Not.Empty);
    }
}
