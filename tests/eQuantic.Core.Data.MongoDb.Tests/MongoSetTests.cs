namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>Covers every member of the <c>ISet&lt;Product&gt;</c> surface (the native <c>MongoSet</c>).</summary>
[TestFixture]
public sealed class MongoSetTests : MongoIntegrationTest
{
    [Test]
    public async Task Insert_stages_the_document_until_commit()
    {
        using var db = NewDatabase();
        var set = Uow(db).CreateSet<Product>();
        var product = Product.New("A", "X", 1, 1m);

        set.Insert(product);
        Assert.That(set.Find(product.Id), Is.Null, "nothing persists before commit");

        await Uow(db).CommitAsync();
        Assert.That(set.Find(product.Id), Is.Not.Null);
    }

    [Test]
    public async Task InsertAsync_stages_the_document_until_commit()
    {
        using var db = NewDatabase();
        var set = Uow(db).CreateSet<Product>();
        var product = Product.New("A", "X", 1, 1m);

        await set.InsertAsync(product);
        await Uow(db).CommitAsync();

        Assert.That(await set.FindAsync(product.Id), Is.Not.Null);
    }

    [Test]
    public async Task Find_returns_by_id_or_null()
    {
        using var db = NewDatabase();
        var product = Product.New("A", "X", 1, 1m);
        await Seed(db, product);
        var set = Uow(db).CreateSet<Product>();

        Assert.That(set.Find(product.Id)!.Name, Is.EqualTo("A"));
        Assert.That(set.Find("missing"), Is.Null);
    }

    [Test]
    public async Task FindAsync_returns_by_id()
    {
        using var db = NewDatabase();
        var product = Product.New("A", "X", 1, 1m);
        await Seed(db, product);
        var set = Uow(db).CreateSet<Product>();

        Assert.That((await set.FindAsync(product.Id))!.Name, Is.EqualTo("A"));
    }

    [Test]
    public async Task Execute_materializes_the_query()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "X", 1, 1m), Product.New("B", "X", 1, 1m));
        var set = Uow(db).CreateSet<Product>();

        Assert.That(set.Execute().Count(), Is.EqualTo(2));
    }

    [Test]
    public async Task Set_is_a_queryable_supporting_linq()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "Books", 1, 1m), Product.New("B", "Food", 1, 1m));
        var set = Uow(db).CreateSet<Product>();

        var books = set.Where(p => p.Category == "Books").ToList();

        Assert.That(books.Single().Name, Is.EqualTo("A"));
    }

    [Test]
    public async Task DeleteMany_removes_matching_documents()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "Books", 1, 1m), Product.New("B", "Food", 1, 1m));
        var set = Uow(db).CreateSet<Product>();

        var deleted = set.DeleteMany(p => p.Category == "Books");

        Assert.That(deleted, Is.EqualTo(1));
        Assert.That(set.Execute().Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task DeleteManyAsync_removes_matching_documents()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "Books", 1, 1m), Product.New("B", "Food", 1, 1m));
        var set = Uow(db).CreateSet<Product>();

        var deleted = await set.DeleteManyAsync(p => p.Category == "Books");

        Assert.That(deleted, Is.EqualTo(1));
    }

    [Test]
    public async Task UpdateMany_sets_matching_documents()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "Books", 1, 1m), Product.New("B", "Books", 1, 1m));
        var set = Uow(db).CreateSet<Product>();

        var updated = set.UpdateMany(p => p.Category == "Books", _ => new Product { Category = "Literature" });

        Assert.That(updated, Is.EqualTo(2));
        Assert.That(set.Where(p => p.Category == "Literature").ToList(), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task UpdateManyAsync_sets_matching_documents()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("A", "Books", 1, 1m), Product.New("B", "Books", 1, 1m));
        var set = Uow(db).CreateSet<Product>();

        var updated = await set.UpdateManyAsync(p => p.Category == "Books", _ => new Product { Category = "Literature" });

        Assert.That(updated, Is.EqualTo(2));
    }
}
