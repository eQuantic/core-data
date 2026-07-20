using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>Covers the <c>MongoUnitOfWork</c> surface: commit variants, transactions, factories and hooks.</summary>
[TestFixture]
public sealed class MongoUnitOfWorkTests : MongoIntegrationTest
{
    [Test]
    public void Commit_flushes_staged_writes()
    {
        using var db = NewDatabase();
        SyncRepo(db).Add(Product.New("A", "X", 1, 1m));

        var affected = Uow(db).Commit();

        Assert.That(affected, Is.EqualTo(1));
        Assert.That(SyncRepo(db).Count(), Is.EqualTo(1));
    }

    [Test]
    public void Commit_with_save_options_flushes_staged_writes()
    {
        using var db = NewDatabase();
        SyncRepo(db).Add(Product.New("A", "X", 1, 1m));

        var affected = Uow(db).Commit(_ => { });

        Assert.That(affected, Is.EqualTo(1));
    }

    [Test]
    public async Task CommitAsync_with_save_options_flushes_staged_writes()
    {
        using var db = NewDatabase();
        await AsyncRepo(db).AddAsync(Product.New("A", "X", 1, 1m));

        var affected = await Uow(db).CommitAsync(_ => { });

        Assert.That(affected, Is.EqualTo(1));
    }

    [Test]
    public void CommitAndRefreshChanges_flushes_staged_writes()
    {
        using var db = NewDatabase();
        SyncRepo(db).Add(Product.New("A", "X", 1, 1m));

        Assert.That(Uow(db).CommitAndRefreshChanges(), Is.EqualTo(1));
    }

    [Test]
    public async Task CommitAndRefreshChangesAsync_flushes_staged_writes()
    {
        using var db = NewDatabase();
        await AsyncRepo(db).AddAsync(Product.New("A", "X", 1, 1m));

        Assert.That(await Uow(db).CommitAndRefreshChangesAsync(), Is.EqualTo(1));
    }

    [Test]
    public void RollbackChanges_discards_staged_writes()
    {
        using var db = NewDatabase();
        SyncRepo(db).Add(Product.New("A", "X", 1, 1m));

        Uow(db).RollbackChanges();

        Assert.That(Uow(db).Commit(), Is.EqualTo(0));
        Assert.That(SyncRepo(db).Count(), Is.EqualTo(0));
    }

    [Test]
    public void GetSaveOptions_returns_an_instance()
    {
        using var db = NewDatabase();
        Assert.That(Uow(db).GetSaveOptions(), Is.Not.Null);
    }

    [Test]
    public async Task Transaction_commit_persists_the_flushed_writes()
    {
        using var db = NewDatabase();
        var uow = Uow(db);

        await uow.BeginTransactionAsync();
        await AsyncRepo(db).AddAsync(Product.New("Tx", "X", 1, 1m));
        await uow.CommitAsync();
        await uow.CommitTransactionAsync();

        Assert.That(await AsyncRepo(db).CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task Transaction_rollback_discards_the_flushed_writes()
    {
        using var db = NewDatabase();
        var uow = Uow(db);

        await uow.BeginTransactionAsync();
        await AsyncRepo(db).AddAsync(Product.New("Tx", "X", 1, 1m));
        await uow.CommitAsync();
        await uow.RollbackTransactionAsync();

        Assert.That(await AsyncRepo(db).CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task GetRepository_resolves_a_working_repository()
    {
        using var db = NewDatabase();
        var product = Product.New("A", "X", 1, 1m);
        await Seed(db, product);

        var repository = Uow(db).GetRepository<Product, string>();

        Assert.That(repository, Is.Not.Null);
        Assert.That(repository.Get(product.Id), Is.Not.Null);
    }

    [Test]
    public void Get_repository_factories_resolve()
    {
        using var db = NewDatabase();
        var uow = Uow(db);

        Assert.That(uow.GetAsyncRepository<Product, string>(), Is.Not.Null);
        Assert.That(uow.GetQueryableRepository<Product, string>(), Is.Not.Null);
        Assert.That(uow.GetAsyncQueryableRepository<Product, string>(), Is.Not.Null);
    }

    [Test]
    public void CreateSet_returns_a_set()
    {
        using var db = NewDatabase();
        Assert.That(Uow(db).CreateSet<Product>(), Is.Not.Null);
    }

    [Test]
    public async Task ApplyCurrentValues_stages_a_replace()
    {
        using var db = NewDatabase();
        var product = Product.New("A", "X", 1, 1m);
        await Seed(db, product);

        var current = Product.New("A", "X", 42, 1m);
        current.SetKey(product.Id);
        Uow(db).ApplyCurrentValues(product, current);
        await Uow(db).CommitAsync();

        Assert.That((await AsyncRepo(db).GetAsync(product.Id))!.Quantity, Is.EqualTo(42));
    }

    [Test]
    public async Task SetModified_stages_a_replace()
    {
        using var db = NewDatabase();
        var product = Product.New("A", "X", 1, 1m);
        await Seed(db, product);

        product.Quantity = 7;
        Uow(db).SetModified(product);
        await Uow(db).CommitAsync();

        Assert.That((await AsyncRepo(db).GetAsync(product.Id))!.Quantity, Is.EqualTo(7));
    }

    [Test]
    public async Task Attach_is_a_noop()
    {
        using var db = NewDatabase();
        var product = Product.New("A", "X", 1, 1m);
        await Seed(db, product);

        Uow(db).Attach(product);

        Assert.That(await Uow(db).CommitAsync(), Is.EqualTo(0));
    }

    [Test]
    public void LoadCollection_is_not_supported()
    {
        using var db = NewDatabase();
        var product = Product.New("A", "X", 1, 1m);

        Assert.That(() => Uow(db).LoadCollection<Product, Product>(product, _ => new List<Product>()),
            Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void LoadCollectionAsync_is_not_supported()
    {
        using var db = NewDatabase();
        var product = Product.New("A", "X", 1, 1m);

        Assert.That(async () => await Uow(db).LoadCollectionAsync<Product, Product>(product, _ => new List<Product>()),
            Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void Reload_is_not_supported()
    {
        using var db = NewDatabase();
        Assert.That(() => Uow(db).Reload(Product.New("A", "X", 1, 1m)), Throws.TypeOf<NotSupportedException>());
    }
}
