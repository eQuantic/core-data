using eQuantic.Linq.Specification;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>Covers every synchronous write member of <c>IWriteRepository&lt;Product&gt;</c>.</summary>
[TestFixture]
public sealed class MongoSyncWriteRepositoryTests : MongoIntegrationTest
{
    [Test]
    public void Add_stages_the_insert_until_commit()
    {
        using var db = NewDatabase();
        var repo = SyncRepo(db);
        var product = Product.New("Keyboard", "Peripherals", 10, 49.90m);

        repo.Add(product);
        Assert.That(repo.Count(), Is.EqualTo(0), "nothing persists before commit");

        Uow(db).Commit();
        Assert.That(repo.Get(product.Id), Is.Not.Null);
    }

    [Test]
    public void AddRange_stages_every_insert()
    {
        using var db = NewDatabase();
        var repo = SyncRepo(db);

        repo.AddRange([Product.New("A", "X", 1, 1m), Product.New("B", "X", 1, 1m)]);
        Uow(db).Commit();

        Assert.That(repo.Count(), Is.EqualTo(2));
    }

    [Test]
    public async Task Modify_stages_a_replace()
    {
        using var db = NewDatabase();
        var repo = SyncRepo(db);
        var product = Product.New("Mouse", "Peripherals", 5, 19.90m);
        await Seed(db, product);

        product.Quantity = 8;
        repo.Modify(product);
        Uow(db).Commit();

        Assert.That(repo.Get(product.Id)!.Quantity, Is.EqualTo(8));
    }

    [Test]
    public async Task Merge_replaces_the_document_with_the_current_values()
    {
        using var db = NewDatabase();
        var repo = SyncRepo(db);
        var persisted = Product.New("Mouse", "Peripherals", 5, 19.90m);
        await Seed(db, persisted);

        var current = Product.New("Mouse", "Peripherals", 99, 19.90m);
        current.SetKey(persisted.Id);
        repo.Merge(persisted, current);
        Uow(db).Commit();

        Assert.That(repo.Get(persisted.Id)!.Quantity, Is.EqualTo(99));
    }

    [Test]
    public async Task Remove_stages_the_delete()
    {
        using var db = NewDatabase();
        var repo = SyncRepo(db);
        var product = Product.New("Cable", "Accessories", 1, 1m);
        await Seed(db, product);

        repo.Remove(product);
        Uow(db).Commit();

        Assert.That(repo.Get(product.Id), Is.Null);
    }

    [Test]
    public async Task TrackItem_does_not_stage_anything()
    {
        using var db = NewDatabase();
        var repo = SyncRepo(db);
        var product = Product.New("Cable", "Accessories", 1, 1m);
        await Seed(db, product);

        repo.TrackItem(product);
        var affected = Uow(db).Commit();

        Assert.That(affected, Is.EqualTo(0));
        Assert.That(repo.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task DeleteMany_by_filter_removes_immediately()
    {
        using var db = NewDatabase();
        var repo = SyncRepo(db);
        await Seed(db, Product.New("A", "Books", 1, 1m), Product.New("B", "Books", 1, 1m), Product.New("C", "Food", 1, 1m));

        var deleted = repo.DeleteMany(p => p.Category == "Books");

        Assert.That(deleted, Is.EqualTo(2));
        Assert.That(repo.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task DeleteMany_by_specification_removes_immediately()
    {
        using var db = NewDatabase();
        var repo = SyncRepo(db);
        await Seed(db, Product.New("A", "Books", 1, 1m), Product.New("B", "Food", 1, 1m));

        var deleted = repo.DeleteMany(new DirectSpecification<Product>(p => p.Category == "Books"));

        Assert.That(deleted, Is.EqualTo(1));
        Assert.That(repo.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task UpdateMany_by_filter_sets_matching_documents()
    {
        using var db = NewDatabase();
        var repo = SyncRepo(db);
        await Seed(db, Product.New("A", "Books", 1, 1m), Product.New("B", "Books", 1, 1m), Product.New("C", "Food", 1, 1m));

        var updated = repo.UpdateMany(p => p.Category == "Books", _ => new Product { Category = "Literature" });

        Assert.That(updated, Is.EqualTo(2));
        Assert.That(repo.GetFiltered(p => p.Category == "Literature").Count(), Is.EqualTo(2));
    }

    [Test]
    public async Task UpdateMany_by_specification_sets_matching_documents()
    {
        using var db = NewDatabase();
        var repo = SyncRepo(db);
        await Seed(db, Product.New("A", "Books", 1, 1m), Product.New("B", "Food", 1, 1m));

        var updated = repo.UpdateMany(new DirectSpecification<Product>(p => p.Category == "Books"),
            _ => new Product { Category = "Literature" });

        Assert.That(updated, Is.EqualTo(1));
        Assert.That(repo.GetFiltered(p => p.Category == "Literature").Count(), Is.EqualTo(1));
    }
}
