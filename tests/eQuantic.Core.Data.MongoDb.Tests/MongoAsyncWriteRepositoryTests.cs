using eQuantic.Linq.Specification;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>Covers every asynchronous write member of <c>IAsyncWriteRepository&lt;Product&gt;</c>.</summary>
[TestFixture]
public sealed class MongoAsyncWriteRepositoryTests : MongoIntegrationTest
{
    [Test]
    public async Task AddAsync_stages_the_insert_until_commit()
    {
        using var db = NewDatabase();
        var repo = AsyncRepo(db);
        var product = Product.New("Keyboard", "Peripherals", 10, 49.90m);

        await repo.AddAsync(product);
        Assert.That(await repo.CountAsync(), Is.EqualTo(0), "nothing persists before commit");

        await Uow(db).CommitAsync();
        Assert.That(await repo.GetAsync(product.Id), Is.Not.Null);
    }

    [Test]
    public async Task AddRangeAsync_stages_every_insert()
    {
        using var db = NewDatabase();
        var repo = AsyncRepo(db);

        await repo.AddRangeAsync([Product.New("A", "X", 1, 1m), Product.New("B", "X", 1, 1m)]);
        await Uow(db).CommitAsync();

        Assert.That(await repo.CountAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task ModifyAsync_stages_a_replace()
    {
        using var db = NewDatabase();
        var repo = AsyncRepo(db);
        var product = Product.New("Mouse", "Peripherals", 5, 19.90m);
        await Seed(db, product);

        product.Quantity = 8;
        await repo.ModifyAsync(product);
        await Uow(db).CommitAsync();

        Assert.That((await repo.GetAsync(product.Id))!.Quantity, Is.EqualTo(8));
    }

    [Test]
    public async Task MergeAsync_replaces_the_document_with_the_current_values()
    {
        using var db = NewDatabase();
        var repo = AsyncRepo(db);
        var persisted = Product.New("Mouse", "Peripherals", 5, 19.90m);
        await Seed(db, persisted);

        var current = Product.New("Mouse", "Peripherals", 99, 19.90m);
        current.SetKey(persisted.Id);
        await repo.MergeAsync(persisted, current);
        await Uow(db).CommitAsync();

        Assert.That((await repo.GetAsync(persisted.Id))!.Quantity, Is.EqualTo(99));
    }

    [Test]
    public async Task RemoveAsync_stages_the_delete()
    {
        using var db = NewDatabase();
        var repo = AsyncRepo(db);
        var product = Product.New("Cable", "Accessories", 1, 1m);
        await Seed(db, product);

        await repo.RemoveAsync(product);
        await Uow(db).CommitAsync();

        Assert.That(await repo.GetAsync(product.Id), Is.Null);
    }

    [Test]
    public async Task DeleteManyAsync_by_filter_removes_immediately()
    {
        using var db = NewDatabase();
        var repo = AsyncRepo(db);
        await Seed(db, Product.New("A", "Books", 1, 1m), Product.New("B", "Books", 1, 1m), Product.New("C", "Food", 1, 1m));

        var deleted = await repo.DeleteManyAsync(p => p.Category == "Books");

        Assert.That(deleted, Is.EqualTo(2));
        Assert.That(await repo.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task DeleteManyAsync_by_specification_removes_immediately()
    {
        using var db = NewDatabase();
        var repo = AsyncRepo(db);
        await Seed(db, Product.New("A", "Books", 1, 1m), Product.New("B", "Food", 1, 1m));

        var deleted = await repo.DeleteManyAsync(new DirectSpecification<Product>(p => p.Category == "Books"));

        Assert.That(deleted, Is.EqualTo(1));
        Assert.That(await repo.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task UpdateManyAsync_by_filter_sets_matching_documents()
    {
        using var db = NewDatabase();
        var repo = AsyncRepo(db);
        await Seed(db, Product.New("A", "Books", 1, 1m), Product.New("B", "Books", 1, 1m), Product.New("C", "Food", 1, 1m));

        var updated = await repo.UpdateManyAsync(p => p.Category == "Books", _ => new Product { Category = "Literature" });

        Assert.That(updated, Is.EqualTo(2));
        var literature = new eQuantic.Core.Data.Repository.Options.QueryOptions<Product>().Where(p => p.Category == "Literature");
        Assert.That(await repo.CountAsync(literature), Is.EqualTo(2));
    }

    [Test]
    public async Task UpdateManyAsync_by_specification_sets_matching_documents()
    {
        using var db = NewDatabase();
        var repo = AsyncRepo(db);
        await Seed(db, Product.New("A", "Books", 1, 1m), Product.New("B", "Food", 1, 1m));

        var updated = await repo.UpdateManyAsync(new DirectSpecification<Product>(p => p.Category == "Books"),
            _ => new Product { Category = "Literature" });

        Assert.That(updated, Is.EqualTo(1));
        var literature = new eQuantic.Core.Data.Repository.Options.QueryOptions<Product>().Where(p => p.Category == "Literature");
        Assert.That(await repo.CountAsync(literature), Is.EqualTo(1));
    }

    [Test]
    public async Task UpdateManyAsync_applies_computed_increments_atomically()
    {
        using var db = NewDatabase();
        var repo = AsyncRepo(db);
        var product = Product.New("Keyboard", "Peripherals", 10, 4m);
        await Seed(db, product);

        var updated = await repo.UpdateManyAsync(p => p.Id == product.Id,
            x => new Product { Quantity = x.Quantity + 5, Price = x.Price * 2m });

        Assert.That(updated, Is.EqualTo(1));
        var loaded = await repo.GetAsync(product.Id);
        Assert.That(loaded!.Quantity, Is.EqualTo(15), "$inc applied");
        Assert.That(loaded.Price, Is.EqualTo(8m), "$mul applied");
    }

    [Test]
    public async Task UpdateManyAsync_pushes_and_pulls_collection_items()
    {
        using var db = NewDatabase();
        var repo = AsyncRepo(db);
        var product = Product.New("Keyboard", "Peripherals", 10, 4m);
        product.Tags = ["old", "keep"];
        await Seed(db, product);

        await repo.UpdateManyAsync(p => p.Id == product.Id, x => new Product { Tags = x.Tags.Append("vip").ToList() });
        var gone = new[] { "old" };
        await repo.UpdateManyAsync(p => p.Id == product.Id, x => new Product { Tags = x.Tags.Except(gone).ToList() });

        var loaded = await repo.GetAsync(product.Id);
        Assert.That(loaded!.Tags, Is.EqualTo(new[] { "keep", "vip" }), "$push then $pullAll applied");
    }
}
