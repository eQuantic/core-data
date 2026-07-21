using System.Net;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Read;
using Microsoft.Azure.Cosmos;

namespace eQuantic.Core.Data.CosmosDb.Tests;

/// <summary>
///     Proves the v5.6 surface against a real emulator: ETag optimistic concurrency, continuation-token paging,
///     streaming, global query filters (per-request factory) and computed patch increments. Same
///     share-a-container model as <see cref="CosmosProviderTests" /> — each test isolates its data under a unique
///     partition value.
/// </summary>
[TestFixture]
public sealed class CosmosEnterpriseTests : CosmosIntegrationTest
{
    private QueryOptions<CosmosProduct> InPartition => new QueryOptions<CosmosProduct>().Where(p => p.Category == Partition);

    // ---------------------------------------------------------------- optimistic concurrency (ETag)

    [Test]
    public async Task A_stale_etag_fails_the_commit_instead_of_silently_winning()
    {
        var product = CosmosProduct.New("Guarded", Partition, 1, 1m);
        await Seed(product);

        var first = (await Repo.GetAsync(product.Id))!;
        var second = (await Repo.GetAsync(product.Id))!;
        Assert.That(first.ETag, Is.Not.Null.And.Not.Empty, "reads populate the concurrency token");

        first.Quantity = 2;
        await Repo.ModifyAsync(first);
        await Uow.CommitAsync();

        second.Quantity = 9;
        await Repo.ModifyAsync(second);
        Assert.That(async () => await Uow.CommitAsync(),
            Throws.InstanceOf<CosmosException>().With.Property("StatusCode").EqualTo(HttpStatusCode.PreconditionFailed));

        Assert.That((await Repo.GetAsync(product.Id))!.Quantity, Is.EqualTo(2), "the first writer's value survives");
    }

    [Test]
    public async Task A_fresh_etag_replaces_conditionally_and_rotates()
    {
        var product = CosmosProduct.New("Versioned", Partition, 1, 1m);
        await Seed(product);

        var loaded = (await Repo.GetAsync(product.Id))!;
        var etag = loaded.ETag;
        loaded.Quantity = 5;
        await Repo.ModifyAsync(loaded);
        await Uow.CommitAsync();

        var reloaded = (await Repo.GetAsync(product.Id))!;
        Assert.That(reloaded.Quantity, Is.EqualTo(5));
        Assert.That(reloaded.ETag, Is.Not.EqualTo(etag), "the token rotates on every replace");
    }

    // ---------------------------------------------------------------- continuation paging + streaming

    [Test]
    public async Task Get_page_walks_the_native_continuation_to_exhaustion()
    {
        await Seed(
            CosmosProduct.New("A", Partition, 1, 1m), CosmosProduct.New("B", Partition, 1, 1m),
            CosmosProduct.New("C", Partition, 1, 1m), CosmosProduct.New("D", Partition, 1, 1m),
            CosmosProduct.New("E", Partition, 1, 1m));

        var pager = (IContinuationReadRepository<CosmosProduct>)Repo;
        var seen = new List<string>();
        string? token = null;
        var pages = 0;

        do
        {
            var page = await pager.GetPageAsync(2, token, InPartition);
            Assert.That(page.Items, Has.Count.LessThanOrEqualTo(2));
            seen.AddRange(page.Items.Select(p => p.Id));
            token = page.ContinuationToken;
            pages++;
        } while (token is not null && pages < 10);

        Assert.That(seen, Has.Count.EqualTo(5));
        Assert.That(seen, Is.Unique, "no document repeats across pages");
        Assert.That(pages, Is.GreaterThanOrEqualTo(3));
    }

    [Test]
    public async Task Get_stream_yields_every_matching_document()
    {
        await Seed(CosmosProduct.New("A", Partition, 1, 1m), CosmosProduct.New("B", Partition, 1, 1m),
            CosmosProduct.New("C", Partition, 1, 1m));

        var seen = new List<CosmosProduct>();
        await foreach (var product in ((IStreamingReadRepository<CosmosProduct>)Repo).GetStreamAsync(InPartition))
        {
            seen.Add(product);
        }

        Assert.That(seen, Has.Count.EqualTo(3));
    }

    // ---------------------------------------------------------------- global query filters (per-request factory)

    [Test]
    public async Task Global_filter_scopes_reads_by_the_requests_tenant()
    {
        var other = "p" + Guid.NewGuid().ToString("N");
        await Seed(CosmosProduct.New("Mine", Partition, 1, 1m), CosmosProduct.New("Theirs", other, 1, 1m));

        Resolve<TenantBox>().Category = Partition;

        Assert.That((await Repo.GetAllAsync()).Select(p => p.Name), Is.EquivalentTo(new[] { "Mine" }),
            "the factory filter scopes the unfiltered read to the tenant");
        var escaped = await Repo.GetAllAsync(new QueryOptions<CosmosProduct>().IgnoringQueryFilters().Where(p => p.Category == other));
        Assert.That(escaped.Select(p => p.Name), Is.EquivalentTo(new[] { "Theirs" }), "IgnoringQueryFilters opts out");
    }

    [Test]
    public async Task Global_filter_scopes_set_based_deletes()
    {
        var other = "p" + Guid.NewGuid().ToString("N");
        await Seed(CosmosProduct.New("Mine", Partition, 99, 1m), CosmosProduct.New("Theirs", other, 99, 1m));

        Resolve<TenantBox>().Category = Partition;
        var removed = await Repo.DeleteManyAsync(p => p.Quantity == 99);

        Assert.That(removed, Is.EqualTo(1), "only the tenant's document is deleted");
        var survivors = await Repo.GetAllAsync(new QueryOptions<CosmosProduct>().IgnoringQueryFilters().Where(p => p.Category == other));
        Assert.That(survivors.Select(p => p.Name), Is.EquivalentTo(new[] { "Theirs" }));
    }

    // ---------------------------------------------------------------- computed updates (native patch)

    [Test]
    public async Task Update_many_applies_the_native_increment()
    {
        var product = CosmosProduct.New("Counted", Partition, 10, 1m);
        await Seed(product);

        var updated = await Repo.UpdateManyAsync(p => p.Category == Partition && p.Id == product.Id,
            x => new CosmosProduct { Quantity = x.Quantity + 5 });

        Assert.That(updated, Is.EqualTo(1));
        Assert.That((await Repo.GetAsync(product.Id))!.Quantity, Is.EqualTo(15), "PatchOperation.Increment applied");
    }
}
