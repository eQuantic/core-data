using eQuantic.Core.Data.MongoDb.Repository;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using FluentAssertions;
using Xunit;

namespace eQuantic.Core.Data.MongoDb.Tests;

[Collection("mongo")]
public sealed class MongoRepositoryTests(MongoServerFixture fixture)
{
    [Fact]
    public async Task Add_and_commit_persist_the_entity()
    {
        using var db = fixture.NewDatabase();
        var repository = db.Resolve<IAsyncRepository<Product, string>>();
        var unitOfWork = db.Resolve<MongoDefaultUnitOfWork>();

        var product = Product.New("Keyboard", "Peripherals", 10, 49.90m);
        await repository.AddAsync(product);
        var affected = await unitOfWork.CommitAsync();

        affected.Should().Be(1);
        var found = await repository.GetAsync(product.Id);
        found.Should().NotBeNull();
        found!.Name.Should().Be("Keyboard");
        found.Price.Should().Be(49.90m);
    }

    [Fact]
    public async Task Modify_and_commit_update_the_entity()
    {
        using var db = fixture.NewDatabase();
        var repository = db.Resolve<IAsyncRepository<Product, string>>();
        var unitOfWork = db.Resolve<MongoDefaultUnitOfWork>();

        var product = Product.New("Mouse", "Peripherals", 5, 19.90m);
        await repository.AddAsync(product);
        await unitOfWork.CommitAsync();

        product.Quantity = 8;
        await repository.ModifyAsync(product);
        await unitOfWork.CommitAsync();

        var found = await repository.GetAsync(product.Id);
        found!.Quantity.Should().Be(8);
    }

    [Fact]
    public async Task Remove_and_commit_delete_the_entity()
    {
        using var db = fixture.NewDatabase();
        var repository = db.Resolve<IAsyncRepository<Product, string>>();
        var unitOfWork = db.Resolve<MongoDefaultUnitOfWork>();

        var product = Product.New("Cable", "Accessories", 100, 3.50m);
        await repository.AddAsync(product);
        await unitOfWork.CommitAsync();

        await repository.RemoveAsync(product);
        await unitOfWork.CommitAsync();

        (await repository.GetAsync(product.Id)).Should().BeNull();
    }

    [Fact]
    public async Task RollbackChanges_discards_staged_writes()
    {
        using var db = fixture.NewDatabase();
        var repository = db.Resolve<IAsyncRepository<Product, string>>();
        var unitOfWork = db.Resolve<MongoDefaultUnitOfWork>();

        await repository.AddAsync(Product.New("Ghost", "None", 1, 1m));
        unitOfWork.RollbackChanges();
        var affected = await unitOfWork.CommitAsync();

        affected.Should().Be(0);
        (await repository.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Query_options_filter_sort_and_page()
    {
        using var db = fixture.NewDatabase();
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

        page.TotalCount.Should().Be(3);
        page.Items.Should().HaveCount(2);
        page.Items.Select(product => product.Name).Should().ContainInOrder("B", "C");
    }

    [Fact]
    public async Task Count_any_and_sum_honour_the_filter()
    {
        using var db = fixture.NewDatabase();
        var repository = db.Resolve<IAsyncRepository<Product, string>>();
        await Seed(db,
            Product.New("A", "Books", 2, 30m),
            Product.New("B", "Books", 3, 10m),
            Product.New("C", "Food", 4, 5m));

        var books = new QueryOptions<Product>().Where(product => product.Category == "Books");

        (await repository.CountAsync(books)).Should().Be(2);
        (await repository.AnyAsync(books)).Should().BeTrue();
        (await repository.SumAsync(product => product.Quantity, books)).Should().Be(5);
    }

    [Fact]
    public async Task DeleteMany_removes_matching_documents_immediately()
    {
        using var db = fixture.NewDatabase();
        var repository = db.Resolve<IAsyncRepository<Product, string>>();
        await Seed(db,
            Product.New("A", "Books", 1, 1m),
            Product.New("B", "Books", 1, 1m),
            Product.New("C", "Food", 1, 1m));

        var deleted = await repository.DeleteManyAsync(product => product.Category == "Books");

        deleted.Should().Be(2);
        (await repository.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task UpdateMany_translates_member_init_to_a_set()
    {
        using var db = fixture.NewDatabase();
        var repository = db.Resolve<IAsyncRepository<Product, string>>();
        await Seed(db,
            Product.New("A", "Books", 1, 1m),
            Product.New("B", "Books", 1, 1m),
            Product.New("C", "Food", 1, 1m));

        var updated = await repository.UpdateManyAsync(
            product => product.Category == "Books",
            _ => new Product { Category = "Literature" });

        updated.Should().Be(2);
        var literature = new QueryOptions<Product>().Where(product => product.Category == "Literature");
        (await repository.CountAsync(literature)).Should().Be(2);
    }

    [Fact]
    public async Task Transaction_commit_persists_the_flushed_writes()
    {
        using var db = fixture.NewDatabase();
        var repository = db.Resolve<IAsyncRepository<Product, string>>();
        var unitOfWork = db.Resolve<MongoDefaultUnitOfWork>();

        await unitOfWork.BeginTransactionAsync();
        await repository.AddAsync(Product.New("Tx", "Books", 1, 1m));
        await unitOfWork.CommitAsync();
        await unitOfWork.CommitTransactionAsync();

        (await repository.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Transaction_rollback_discards_the_flushed_writes()
    {
        using var db = fixture.NewDatabase();
        var repository = db.Resolve<IAsyncRepository<Product, string>>();
        var unitOfWork = db.Resolve<MongoDefaultUnitOfWork>();

        await unitOfWork.BeginTransactionAsync();
        await repository.AddAsync(Product.New("Tx", "Books", 1, 1m));
        await unitOfWork.CommitAsync();
        await unitOfWork.RollbackTransactionAsync();

        (await repository.CountAsync()).Should().Be(0);
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
