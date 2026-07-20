using eQuantic.Core.Data.MongoDb.Repository;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;

namespace eQuantic.Core.Data.MongoDb.Tests;

[TestFixture]
public sealed class MongoRepositoryTests : MongoIntegrationTest
{
    [Test]
    public async Task Add_and_commit_persist_the_entity()
    {
        using var db = MongoTestServer.NewDatabase();
        var repository = db.Resolve<IAsyncRepository<Product, string>>();
        var unitOfWork = db.Resolve<MongoDefaultUnitOfWork>();

        var product = Product.New("Keyboard", "Peripherals", 10, 49.90m);
        await repository.AddAsync(product);
        var affected = await unitOfWork.CommitAsync();

        Assert.That(affected, Is.EqualTo(1));
        var found = await repository.GetAsync(product.Id);
        Assert.That(found, Is.Not.Null);
        Assert.That(found!.Name, Is.EqualTo("Keyboard"));
        Assert.That(found.Price, Is.EqualTo(49.90m));
    }

    [Test]
    public async Task Modify_and_commit_update_the_entity()
    {
        using var db = MongoTestServer.NewDatabase();
        var repository = db.Resolve<IAsyncRepository<Product, string>>();
        var unitOfWork = db.Resolve<MongoDefaultUnitOfWork>();

        var product = Product.New("Mouse", "Peripherals", 5, 19.90m);
        await repository.AddAsync(product);
        await unitOfWork.CommitAsync();

        product.Quantity = 8;
        await repository.ModifyAsync(product);
        await unitOfWork.CommitAsync();

        var found = await repository.GetAsync(product.Id);
        Assert.That(found!.Quantity, Is.EqualTo(8));
    }

    [Test]
    public async Task Remove_and_commit_delete_the_entity()
    {
        using var db = MongoTestServer.NewDatabase();
        var repository = db.Resolve<IAsyncRepository<Product, string>>();
        var unitOfWork = db.Resolve<MongoDefaultUnitOfWork>();

        var product = Product.New("Cable", "Accessories", 100, 3.50m);
        await repository.AddAsync(product);
        await unitOfWork.CommitAsync();

        await repository.RemoveAsync(product);
        await unitOfWork.CommitAsync();

        Assert.That(await repository.GetAsync(product.Id), Is.Null);
    }

    [Test]
    public async Task RollbackChanges_discards_staged_writes()
    {
        using var db = MongoTestServer.NewDatabase();
        var repository = db.Resolve<IAsyncRepository<Product, string>>();
        var unitOfWork = db.Resolve<MongoDefaultUnitOfWork>();

        await repository.AddAsync(Product.New("Ghost", "None", 1, 1m));
        unitOfWork.RollbackChanges();
        var affected = await unitOfWork.CommitAsync();

        Assert.That(affected, Is.EqualTo(0));
        Assert.That(await repository.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task Query_options_filter_sort_and_page()
    {
        using var db = MongoTestServer.NewDatabase();
        var repository = db.Resolve<IAsyncRepository<Product, string>>();
        await Seed(db,
            Product.New("A", "Books", 1, 30m),
            Product.New("B", "Books", 2, 10m),
            Product.New("C", "Books", 3, 20m),
            Product.New("D", "Food", 4, 5m));

        var options = new QueryOptions<Product>()
            .Where(product => product.Category == "Books")
            .OrderBy(product => product.Price);
        var page = await repository.GetPagedAsync(new PageRequest(1, 2), options);

        Assert.That(page.TotalCount, Is.EqualTo(3));
        Assert.That(page.Items, Has.Count.EqualTo(2));
        Assert.That(page.Items.Select(product => product.Name).ToArray(), Is.EqualTo(new[] { "B", "C" }));
    }

    [Test]
    public async Task Count_any_and_sum_honour_the_filter()
    {
        using var db = MongoTestServer.NewDatabase();
        var repository = db.Resolve<IAsyncRepository<Product, string>>();
        await Seed(db,
            Product.New("A", "Books", 2, 30m),
            Product.New("B", "Books", 3, 10m),
            Product.New("C", "Food", 4, 5m));

        var books = new QueryOptions<Product>().Where(product => product.Category == "Books");

        Assert.That(await repository.CountAsync(books), Is.EqualTo(2));
        Assert.That(await repository.AnyAsync(books), Is.True);
        Assert.That(await repository.SumAsync(product => product.Quantity, books), Is.EqualTo(5));
    }

    [Test]
    public async Task DeleteMany_removes_matching_documents_immediately()
    {
        using var db = MongoTestServer.NewDatabase();
        var repository = db.Resolve<IAsyncRepository<Product, string>>();
        await Seed(db,
            Product.New("A", "Books", 1, 1m),
            Product.New("B", "Books", 1, 1m),
            Product.New("C", "Food", 1, 1m));

        var deleted = await repository.DeleteManyAsync(product => product.Category == "Books");

        Assert.That(deleted, Is.EqualTo(2));
        Assert.That(await repository.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task UpdateMany_translates_member_init_to_a_set()
    {
        using var db = MongoTestServer.NewDatabase();
        var repository = db.Resolve<IAsyncRepository<Product, string>>();
        await Seed(db,
            Product.New("A", "Books", 1, 1m),
            Product.New("B", "Books", 1, 1m),
            Product.New("C", "Food", 1, 1m));

        var updated = await repository.UpdateManyAsync(
            product => product.Category == "Books",
            _ => new Product { Category = "Literature" });

        Assert.That(updated, Is.EqualTo(2));
        var literature = new QueryOptions<Product>().Where(product => product.Category == "Literature");
        Assert.That(await repository.CountAsync(literature), Is.EqualTo(2));
    }

    [Test]
    public async Task Transaction_commit_persists_the_flushed_writes()
    {
        using var db = MongoTestServer.NewDatabase();
        var repository = db.Resolve<IAsyncRepository<Product, string>>();
        var unitOfWork = db.Resolve<MongoDefaultUnitOfWork>();

        await unitOfWork.BeginTransactionAsync();
        await repository.AddAsync(Product.New("Tx", "Books", 1, 1m));
        await unitOfWork.CommitAsync();
        await unitOfWork.CommitTransactionAsync();

        Assert.That(await repository.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task Transaction_rollback_discards_the_flushed_writes()
    {
        using var db = MongoTestServer.NewDatabase();
        var repository = db.Resolve<IAsyncRepository<Product, string>>();
        var unitOfWork = db.Resolve<MongoDefaultUnitOfWork>();

        await unitOfWork.BeginTransactionAsync();
        await repository.AddAsync(Product.New("Tx", "Books", 1, 1m));
        await unitOfWork.CommitAsync();
        await unitOfWork.RollbackTransactionAsync();

        Assert.That(await repository.CountAsync(), Is.EqualTo(0));
    }

    private static async Task Seed(MongoTestDatabase db, params Product[] products)
    {
        var repository = db.Resolve<IAsyncRepository<Product, string>>();
        var unitOfWork = db.Resolve<MongoDefaultUnitOfWork>();
        foreach (var product in products)
        {
            await repository.AddAsync(product);
        }

        await unitOfWork.CommitAsync();
    }
}
