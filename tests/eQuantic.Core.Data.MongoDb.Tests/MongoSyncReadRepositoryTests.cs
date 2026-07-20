using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Linq.Specification;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>Covers every synchronous read member of <c>IReadRepository&lt;Product, string&gt;</c>.</summary>
[TestFixture]
public sealed class MongoSyncReadRepositoryTests : MongoIntegrationTest
{
    [Test]
    public async Task Get_returns_the_entity_by_id()
    {
        using var db = NewDatabase();
        var product = Product.New("Keyboard", "Peripherals", 10, 49.90m);
        await Seed(db, product);

        var found = SyncRepo(db).Get(product.Id);

        Assert.That(found, Is.Not.Null);
        Assert.That(found!.Name, Is.EqualTo("Keyboard"));
    }

    [Test]
    public async Task Get_returns_null_when_the_id_is_missing()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("Keyboard", "Peripherals", 10, 49.90m));

        Assert.That(SyncRepo(db).Get("missing"), Is.Null);
    }

    [Test]
    public async Task Get_applies_the_options_scope_filter()
    {
        using var db = NewDatabase();
        var product = Product.New("Keyboard", "Peripherals", 10, 49.90m);
        await Seed(db, product);

        var scoped = new QueryOptions<Product>().Where(p => p.Category == "Other");
        Assert.That(SyncRepo(db).Get(product.Id, scoped), Is.Null);
    }

    [Test]
    public async Task GetAll_returns_every_document()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "X", 1, 1m), Product.New("B", "X", 1, 1m));

        Assert.That(SyncRepo(db).GetAll().Count(), Is.EqualTo(2));
    }

    [Test]
    public async Task GetAll_honours_options_filter_and_sort()
    {
        using var db = NewDatabase();
        await Seed(db,
            Product.New("A", "Books", 1, 30m),
            Product.New("B", "Books", 1, 10m),
            Product.New("C", "Food", 1, 5m));

        var options = new QueryOptions<Product>().Where(p => p.Category == "Books").OrderBy(p => p.Price);
        var names = SyncRepo(db).GetAll(options).Select(p => p.Name).ToArray();

        Assert.That(names, Is.EqualTo(new[] { "B", "A" }));
    }

    [Test]
    public async Task GetFiltered_returns_matching_documents()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "Books", 1, 1m), Product.New("B", "Food", 1, 1m));

        var result = SyncRepo(db).GetFiltered(p => p.Category == "Books");

        Assert.That(result.Single().Name, Is.EqualTo("A"));
    }

    [Test]
    public async Task AllMatching_applies_the_specification()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "Books", 1, 1m), Product.New("B", "Food", 1, 1m));

        var spec = new DirectSpecification<Product>(p => p.Category == "Books");
        var result = SyncRepo(db).AllMatching(spec);

        Assert.That(result.Single().Name, Is.EqualTo("A"));
    }

    [Test]
    public async Task GetMapped_projects_the_results()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "X", 1, 1m), Product.New("B", "X", 1, 1m));

        var names = SyncRepo(db).GetMapped(p => p.Name);

        Assert.That(names.OrderBy(n => n), Is.EqualTo(new[] { "A", "B" }));
    }

    [Test]
    public async Task GetFirst_returns_the_first_by_sort()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "X", 1, 30m), Product.New("B", "X", 1, 10m));

        var options = new QueryOptions<Product>().OrderBy(p => p.Price);
        Assert.That(SyncRepo(db).GetFirst(options)!.Name, Is.EqualTo("B"));
    }

    [Test]
    public async Task GetFirst_returns_null_when_nothing_matches()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "X", 1, 1m));

        var options = new QueryOptions<Product>().Where(p => p.Category == "None");
        Assert.That(SyncRepo(db).GetFirst(options), Is.Null);
    }

    [Test]
    public async Task GetFirstMapped_projects_the_first()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "X", 1, 30m), Product.New("B", "X", 1, 10m));

        var options = new QueryOptions<Product>().OrderBy(p => p.Price);
        Assert.That(SyncRepo(db).GetFirstMapped(p => p.Name, options), Is.EqualTo("B"));
    }

    [Test]
    public async Task GetSingle_returns_the_only_match()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "Books", 1, 1m), Product.New("B", "Food", 1, 1m));

        var options = new QueryOptions<Product>().Where(p => p.Category == "Books");
        Assert.That(SyncRepo(db).GetSingle(options)!.Name, Is.EqualTo("A"));
    }

    [Test]
    public async Task GetSingle_returns_null_when_nothing_matches()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "Books", 1, 1m));

        var options = new QueryOptions<Product>().Where(p => p.Category == "None");
        Assert.That(SyncRepo(db).GetSingle(options), Is.Null);
    }

    [Test]
    public async Task GetPaged_returns_the_page_and_total()
    {
        using var db = NewDatabase();
        await Seed(db,
            Product.New("A", "Books", 1, 10m),
            Product.New("B", "Books", 1, 20m),
            Product.New("C", "Books", 1, 30m));

        var options = new QueryOptions<Product>().OrderBy(p => p.Price);
        var page = SyncRepo(db).GetPaged(new PageRequest(2, 2), options);

        Assert.That(page.TotalCount, Is.EqualTo(3));
        Assert.That(page.Items.Single().Name, Is.EqualTo("C"));
    }

    [Test]
    public async Task GetPaged_orders_by_id_when_no_sort_is_given()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "X", 1, 1m), Product.New("B", "X", 1, 1m), Product.New("C", "X", 1, 1m));

        var page = SyncRepo(db).GetPaged(new PageRequest(1, 2));

        Assert.That(page.TotalCount, Is.EqualTo(3));
        Assert.That(page.Items, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task GetPaged_mapped_projects_the_page()
    {
        using var db = NewDatabase();
        await Seed(db,
            Product.New("A", "X", 1, 10m),
            Product.New("B", "X", 1, 20m),
            Product.New("C", "X", 1, 30m));

        var options = new QueryOptions<Product>().OrderBy(p => p.Price);
        var page = SyncRepo(db).GetPaged(new PageRequest(1, 2), p => p.Name, options);

        Assert.That(page.TotalCount, Is.EqualTo(3));
        Assert.That(page.Items, Is.EqualTo(new[] { "A", "B" }));
    }

    [Test]
    public async Task Count_counts_all_then_filtered()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "Books", 1, 1m), Product.New("B", "Food", 1, 1m));

        Assert.That(SyncRepo(db).Count(), Is.EqualTo(2));
        Assert.That(SyncRepo(db).Count(new QueryOptions<Product>().Where(p => p.Category == "Books")), Is.EqualTo(1));
    }

    [Test]
    public async Task Any_reflects_whether_documents_match()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "Books", 1, 1m));

        Assert.That(SyncRepo(db).Any(), Is.True);
        Assert.That(SyncRepo(db).Any(new QueryOptions<Product>().Where(p => p.Category == "None")), Is.False);
    }

    [Test]
    public async Task All_reflects_whether_every_document_satisfies_the_predicate()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "X", 1, 10m), Product.New("B", "X", 1, 20m));

        Assert.That(SyncRepo(db).All(p => p.Price > 0), Is.True);
        Assert.That(SyncRepo(db).All(p => p.Price > 15), Is.False);
    }

    [Test]
    public async Task Sum_computes_every_numeric_overload()
    {
        using var db = NewDatabase();
        await Seed(db,
            Product.New("A", "X", 2, 1.5m, weight: 10, rating: 1.5, discount: 0.5f),
            Product.New("B", "X", 3, 2.5m, weight: 20, rating: 2.5, discount: 1.5f));
        var repo = SyncRepo(db);

        Assert.That(repo.Sum(p => p.Quantity), Is.EqualTo(5));
        Assert.That(repo.Sum(p => (int?)p.Quantity), Is.EqualTo(5));
        Assert.That(repo.Sum(p => p.Weight), Is.EqualTo(30L));
        Assert.That(repo.Sum(p => (long?)p.Weight), Is.EqualTo(30L));
        Assert.That(repo.Sum(p => p.Rating), Is.EqualTo(4.0).Within(1e-9));
        Assert.That(repo.Sum(p => (double?)p.Rating), Is.EqualTo(4.0).Within(1e-9));
        Assert.That(repo.Sum(p => p.Discount), Is.EqualTo(2.0f).Within(1e-6f));
        Assert.That(repo.Sum(p => (float?)p.Discount), Is.EqualTo(2.0f).Within(1e-6f));
        Assert.That(repo.Sum(p => p.Price), Is.EqualTo(4.0m));
        Assert.That(repo.Sum(p => (decimal?)p.Price), Is.EqualTo(4.0m));
    }
}
