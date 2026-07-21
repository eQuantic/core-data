using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Read;
using eQuantic.Linq.Specification;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>Covers every asynchronous read member of <c>IAsyncReadRepository&lt;Product, string&gt;</c>.</summary>
[TestFixture]
public sealed class MongoAsyncReadRepositoryTests : MongoIntegrationTest
{
    [Test]
    public async Task GetAsync_returns_the_entity_by_id()
    {
        using var db = NewDatabase();
        var product = Product.New("Keyboard", "Peripherals", 10, 49.90m);
        await Seed(db, product);

        var found = await AsyncRepo(db).GetAsync(product.Id);

        Assert.That(found, Is.Not.Null);
        Assert.That(found!.Name, Is.EqualTo("Keyboard"));
    }

    [Test]
    public async Task GetAsync_returns_null_when_the_id_is_missing()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("Keyboard", "Peripherals", 10, 49.90m));

        Assert.That(await AsyncRepo(db).GetAsync("missing"), Is.Null);
    }

    [Test]
    public async Task GetStreamAsync_streams_the_matching_documents()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "X", 1, 1m), Product.New("B", "X", 1, 1m), Product.New("C", "Y", 1, 1m));

        var stream = ((IStreamingReadRepository<Product>)AsyncRepo(db))
            .GetStreamAsync(new QueryOptions<Product>().Where(p => p.Category == "X"));

        var seen = new List<Product>();
        await foreach (var product in stream)
        {
            seen.Add(product);
        }

        Assert.That(seen.Select(p => p.Name), Is.EquivalentTo(new[] { "A", "B" }));
    }

    [Test]
    public async Task GetPageAsync_walks_the_keyset_to_exhaustion()
    {
        using var db = NewDatabase();
        await Seed(db,
            Product.New("A", "X", 1, 1m), Product.New("B", "X", 1, 1m), Product.New("C", "X", 1, 1m),
            Product.New("D", "X", 1, 1m), Product.New("E", "X", 1, 1m), Product.New("F", "Y", 1, 1m));

        var pager = (IContinuationReadRepository<Product>)AsyncRepo(db);
        var options = new QueryOptions<Product>().Where(p => p.Category == "X");
        var seen = new List<string>();
        string? token = null;
        var pages = 0;

        do
        {
            var page = await pager.GetPageAsync(2, token, options);
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
    public async Task Explain_renders_the_aggregation_pipeline()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("Keyboard", "Peripherals", 10, 49.90m));
        var explainable = (IExplainableRepository<Product>)AsyncRepo(db);

        var plan = explainable.Explain(new QueryOptions<Product>().Where(p => p.Category == "Peripherals"));

        Assert.That(plan.Provider, Is.EqualTo("MongoDb"));
        Assert.That(plan.Statement, Is.Not.Empty);
        Assert.That(plan.ClientEvaluation, Is.False);
    }

    [Test]
    public async Task GetAsync_applies_the_options_scope_filter()
    {
        using var db = NewDatabase();
        var product = Product.New("Keyboard", "Peripherals", 10, 49.90m);
        await Seed(db, product);

        var scoped = new QueryOptions<Product>().Where(p => p.Category == "Other");
        Assert.That(await AsyncRepo(db).GetAsync(product.Id, scoped), Is.Null);
    }

    [Test]
    public async Task GetAllAsync_returns_every_document()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "X", 1, 1m), Product.New("B", "X", 1, 1m));

        Assert.That((await AsyncRepo(db).GetAllAsync()).Count(), Is.EqualTo(2));
    }

    [Test]
    public async Task GetAllAsync_honours_options_filter_and_sort()
    {
        using var db = NewDatabase();
        await Seed(db,
            Product.New("A", "Books", 1, 30m),
            Product.New("B", "Books", 1, 10m),
            Product.New("C", "Food", 1, 5m));

        var options = new QueryOptions<Product>().Where(p => p.Category == "Books").OrderBy(p => p.Price);
        var names = (await AsyncRepo(db).GetAllAsync(options)).Select(p => p.Name).ToArray();

        Assert.That(names, Is.EqualTo(new[] { "B", "A" }));
    }

    [Test]
    public async Task GetFilteredAsync_returns_matching_documents()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "Books", 1, 1m), Product.New("B", "Food", 1, 1m));

        var result = await AsyncRepo(db).GetFilteredAsync(p => p.Category == "Books");

        Assert.That(result.Single().Name, Is.EqualTo("A"));
    }

    [Test]
    public async Task AllMatchingAsync_applies_the_specification()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "Books", 1, 1m), Product.New("B", "Food", 1, 1m));

        var spec = new DirectSpecification<Product>(p => p.Category == "Books");
        var result = await AsyncRepo(db).AllMatchingAsync(spec);

        Assert.That(result.Single().Name, Is.EqualTo("A"));
    }

    [Test]
    public async Task GetMappedAsync_projects_the_results()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "X", 1, 1m), Product.New("B", "X", 1, 1m));

        var names = await AsyncRepo(db).GetMappedAsync(p => p.Name);

        Assert.That(names.OrderBy(n => n), Is.EqualTo(new[] { "A", "B" }));
    }

    [Test]
    public async Task GetFirstAsync_returns_the_first_by_sort()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "X", 1, 30m), Product.New("B", "X", 1, 10m));

        var options = new QueryOptions<Product>().OrderBy(p => p.Price);
        Assert.That((await AsyncRepo(db).GetFirstAsync(options))!.Name, Is.EqualTo("B"));
    }

    [Test]
    public async Task GetFirstAsync_returns_null_when_nothing_matches()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "X", 1, 1m));

        var options = new QueryOptions<Product>().Where(p => p.Category == "None");
        Assert.That(await AsyncRepo(db).GetFirstAsync(options), Is.Null);
    }

    [Test]
    public async Task GetFirstMappedAsync_projects_the_first()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "X", 1, 30m), Product.New("B", "X", 1, 10m));

        var options = new QueryOptions<Product>().OrderBy(p => p.Price);
        Assert.That(await AsyncRepo(db).GetFirstMappedAsync(p => p.Name, options), Is.EqualTo("B"));
    }

    [Test]
    public async Task GetSingleAsync_returns_the_only_match()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "Books", 1, 1m), Product.New("B", "Food", 1, 1m));

        var options = new QueryOptions<Product>().Where(p => p.Category == "Books");
        Assert.That((await AsyncRepo(db).GetSingleAsync(options))!.Name, Is.EqualTo("A"));
    }

    [Test]
    public async Task GetSingleAsync_returns_null_when_nothing_matches()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "Books", 1, 1m));

        var options = new QueryOptions<Product>().Where(p => p.Category == "None");
        Assert.That(await AsyncRepo(db).GetSingleAsync(options), Is.Null);
    }

    [Test]
    public async Task GetPagedAsync_returns_the_page_and_total()
    {
        using var db = NewDatabase();
        await Seed(db,
            Product.New("A", "Books", 1, 10m),
            Product.New("B", "Books", 1, 20m),
            Product.New("C", "Books", 1, 30m));

        var options = new QueryOptions<Product>().OrderBy(p => p.Price);
        var page = await AsyncRepo(db).GetPagedAsync(new PageRequest(2, 2), options);

        Assert.That(page.TotalCount, Is.EqualTo(3));
        Assert.That(page.Items.Single().Name, Is.EqualTo("C"));
    }

    [Test]
    public async Task GetPagedAsync_orders_by_id_when_no_sort_is_given()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "X", 1, 1m), Product.New("B", "X", 1, 1m), Product.New("C", "X", 1, 1m));

        var page = await AsyncRepo(db).GetPagedAsync(new PageRequest(1, 2));

        Assert.That(page.TotalCount, Is.EqualTo(3));
        Assert.That(page.Items, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task GetPagedAsync_mapped_projects_the_page()
    {
        using var db = NewDatabase();
        await Seed(db,
            Product.New("A", "X", 1, 10m),
            Product.New("B", "X", 1, 20m),
            Product.New("C", "X", 1, 30m));

        var options = new QueryOptions<Product>().OrderBy(p => p.Price);
        var page = await AsyncRepo(db).GetPagedAsync(new PageRequest(1, 2), p => p.Name, options);

        Assert.That(page.TotalCount, Is.EqualTo(3));
        Assert.That(page.Items, Is.EqualTo(new[] { "A", "B" }));
    }

    [Test]
    public async Task CountAsync_counts_all_then_filtered()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "Books", 1, 1m), Product.New("B", "Food", 1, 1m));

        Assert.That(await AsyncRepo(db).CountAsync(), Is.EqualTo(2));
        Assert.That(await AsyncRepo(db).CountAsync(new QueryOptions<Product>().Where(p => p.Category == "Books")), Is.EqualTo(1));
    }

    [Test]
    public async Task AnyAsync_reflects_whether_documents_match()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "Books", 1, 1m));

        Assert.That(await AsyncRepo(db).AnyAsync(), Is.True);
        Assert.That(await AsyncRepo(db).AnyAsync(new QueryOptions<Product>().Where(p => p.Category == "None")), Is.False);
    }

    [Test]
    public async Task AllAsync_reflects_whether_every_document_satisfies_the_predicate()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "X", 1, 10m), Product.New("B", "X", 1, 20m));

        Assert.That(await AsyncRepo(db).AllAsync(p => p.Price > 0), Is.True);
        Assert.That(await AsyncRepo(db).AllAsync(p => p.Price > 15), Is.False);
    }

    [Test]
    public async Task SumAsync_computes_every_numeric_overload()
    {
        using var db = NewDatabase();
        await Seed(db,
            Product.New("A", "X", 2, 1.5m, weight: 10, rating: 1.5, discount: 0.5f),
            Product.New("B", "X", 3, 2.5m, weight: 20, rating: 2.5, discount: 1.5f));
        var repo = AsyncRepo(db);

        Assert.That(await repo.SumAsync(p => p.Quantity), Is.EqualTo(5));
        Assert.That(await repo.SumAsync(p => (int?)p.Quantity), Is.EqualTo(5));
        Assert.That(await repo.SumAsync(p => p.Weight), Is.EqualTo(30L));
        Assert.That(await repo.SumAsync(p => (long?)p.Weight), Is.EqualTo(30L));
        Assert.That(await repo.SumAsync(p => p.Rating), Is.EqualTo(4.0).Within(1e-9));
        Assert.That(await repo.SumAsync(p => (double?)p.Rating), Is.EqualTo(4.0).Within(1e-9));
        Assert.That(await repo.SumAsync(p => p.Discount), Is.EqualTo(2.0f).Within(1e-6f));
        Assert.That(await repo.SumAsync(p => (float?)p.Discount), Is.EqualTo(2.0f).Within(1e-6f));
        Assert.That(await repo.SumAsync(p => p.Price), Is.EqualTo(4.0m));
        Assert.That(await repo.SumAsync(p => (decimal?)p.Price), Is.EqualTo(4.0m));
    }
}
