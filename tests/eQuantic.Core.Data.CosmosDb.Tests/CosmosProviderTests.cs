using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;

namespace eQuantic.Core.Data.CosmosDb.Tests;

/// <summary>
///     Proves the native Cosmos provider end to end against a real emulator. Focused (not per-method exhaustive)
///     because Cosmos container creation is slow; each test uses its own container.
/// </summary>
[TestFixture]
public sealed class CosmosProviderTests : CosmosIntegrationTest
{
    [Test]
    public async Task Add_and_commit_then_get_returns_the_entity()
    {
        using var db = NewDatabase();
        var repository = Repo(db);
        var product = CosmosProduct.New("Keyboard", "Peripherals", 10, 49.90m);

        await repository.AddAsync(product);
        var affected = await Uow(db).CommitAsync();

        Assert.That(affected, Is.EqualTo(1));
        var found = await repository.GetAsync(product.Id);
        Assert.That(found, Is.Not.Null);
        Assert.That(found!.Name, Is.EqualTo("Keyboard"));
    }

    [Test]
    public async Task Modify_and_commit_updates_the_entity()
    {
        using var db = NewDatabase();
        var repository = Repo(db);
        var product = CosmosProduct.New("Mouse", "Peripherals", 5, 19.90m);
        await Seed(db, product);

        product.Quantity = 8;
        await repository.ModifyAsync(product);
        await Uow(db).CommitAsync();

        Assert.That((await repository.GetAsync(product.Id))!.Quantity, Is.EqualTo(8));
    }

    [Test]
    public async Task Remove_and_commit_deletes_the_entity()
    {
        using var db = NewDatabase();
        var repository = Repo(db);
        var product = CosmosProduct.New("Cable", "Accessories", 1, 1m);
        await Seed(db, product);

        await repository.RemoveAsync(product);
        await Uow(db).CommitAsync();

        Assert.That(await repository.GetAsync(product.Id), Is.Null);
    }

    [Test]
    public async Task Query_options_filter_sort_and_page()
    {
        using var db = NewDatabase();
        await Seed(db,
            CosmosProduct.New("A", "Books", 1, 30m),
            CosmosProduct.New("B", "Books", 2, 10m),
            CosmosProduct.New("C", "Books", 3, 20m),
            CosmosProduct.New("D", "Food", 4, 5m));

        var options = new QueryOptions<CosmosProduct>().Where(p => p.Category == "Books").OrderBy(p => p.Price);
        var page = await Repo(db).GetPagedAsync(new PageRequest(1, 2), options);

        Assert.That(page.TotalCount, Is.EqualTo(3));
        Assert.That(page.Items.Select(p => p.Name).ToArray(), Is.EqualTo(new[] { "B", "C" }));
    }

    [Test]
    public async Task Count_any_and_sum_honour_the_filter()
    {
        using var db = NewDatabase();
        await Seed(db,
            CosmosProduct.New("A", "Books", 2, 30m),
            CosmosProduct.New("B", "Books", 3, 10m),
            CosmosProduct.New("C", "Food", 4, 5m));

        var books = new QueryOptions<CosmosProduct>().Where(p => p.Category == "Books");

        Assert.That(await Repo(db).CountAsync(books), Is.EqualTo(2));
        Assert.That(await Repo(db).AnyAsync(books), Is.True);
        Assert.That(await Repo(db).SumAsync(p => p.Quantity, books), Is.EqualTo(5));
    }

    [Test]
    public async Task DeleteMany_removes_matching_documents()
    {
        using var db = NewDatabase();
        await Seed(db,
            CosmosProduct.New("A", "Books", 1, 1m),
            CosmosProduct.New("B", "Books", 1, 1m),
            CosmosProduct.New("C", "Food", 1, 1m));

        var deleted = await Repo(db).DeleteManyAsync(p => p.Category == "Books");

        Assert.That(deleted, Is.EqualTo(2));
        Assert.That(await Repo(db).CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task UpdateMany_patches_matching_documents()
    {
        using var db = NewDatabase();
        await Seed(db,
            CosmosProduct.New("A", "Books", 1, 1m),
            CosmosProduct.New("B", "Books", 1, 1m),
            CosmosProduct.New("C", "Food", 1, 1m));

        var updated = await Repo(db).UpdateManyAsync(p => p.Name == "A", _ => new CosmosProduct { Quantity = 99 });

        Assert.That(updated, Is.EqualTo(1));
        var a = new QueryOptions<CosmosProduct>().Where(p => p.Name == "A");
        Assert.That((await Repo(db).GetSingleAsync(a))!.Quantity, Is.EqualTo(99));
    }

    [Test]
    public async Task Transaction_commit_persists_the_single_partition_batch()
    {
        using var db = NewDatabase();
        var repository = Repo(db);
        var unitOfWork = Uow(db);

        await unitOfWork.BeginTransactionAsync();
        await repository.AddAsync(CosmosProduct.New("Tx1", "Books", 1, 1m));
        await repository.AddAsync(CosmosProduct.New("Tx2", "Books", 1, 1m));
        await unitOfWork.CommitAsync();
        await unitOfWork.CommitTransactionAsync();

        Assert.That(await repository.CountAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task Migration_runner_ensures_the_container_and_applies_once()
    {
        using var db = NewDatabase(createContainer: false, typeof(CosmosProductsSetupMigration).Assembly);
        var runner = db.Resolve<IMigrationRunner>();

        Assert.That(await runner.RunAsync(), Is.GreaterThanOrEqualTo(1));
        Assert.That(await runner.RunAsync(), Is.EqualTo(0));

        // the container the migration ensured is usable
        await Seed(db, CosmosProduct.New("A", "Books", 1, 1m));
        Assert.That(await Repo(db).CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task Migration_declares_the_composite_index()
    {
        using var db = NewDatabase(createContainer: false, typeof(CosmosProductsSetupMigration).Assembly);
        await db.Resolve<IMigrationRunner>().RunAsync();

        var properties = (await db.Database.GetContainer(db.ContainerName).ReadContainerAsync()).Resource;
        Assert.That(properties.IndexingPolicy.CompositeIndexes, Is.Not.Empty);
    }
}
