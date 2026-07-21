using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>
///     Exercises global query filters against a real MongoDB: reads scoped by the registered filter, the
///     <c>IgnoringQueryFilters</c> opt-out, and set-based writes that stay inside the filter's scope.
/// </summary>
[TestFixture]
public sealed class MongoQueryFilterTests : MongoIntegrationTest
{
    private static MongoTestDatabase FilteredDatabase() =>
        NewDatabase(services => services.AddSingleton(new QueryFilters().For<Product>(p => p.Category == "X")));

    [Test]
    public async Task Global_filter_scopes_every_read_and_ignoring_opts_out()
    {
        using var db = FilteredDatabase();
        var repo = AsyncRepo(db);
        await Seed(db, Product.New("A", "X", 1, 1m), Product.New("B", "X", 1, 1m), Product.New("C", "Y", 1, 1m));

        Assert.That((await repo.GetAllAsync()).Select(p => p.Category), Is.All.EqualTo("X"));
        Assert.That(await repo.CountAsync(), Is.EqualTo(2));
        Assert.That(await repo.CountAsync(new QueryOptions<Product>().IgnoringQueryFilters()), Is.EqualTo(3));
    }

    [Test]
    public async Task Global_filter_scopes_set_based_writes()
    {
        using var db = FilteredDatabase();
        var repo = AsyncRepo(db);
        await Seed(db, Product.New("A", "X", 1, 1m), Product.New("C", "Y", 1, 1m));

        var removed = await repo.DeleteManyAsync(p => p.Quantity == 1);

        Assert.That(removed, Is.EqualTo(1), "only the scoped document is deleted");
        var survivors = await repo.GetAllAsync(new QueryOptions<Product>().IgnoringQueryFilters());
        Assert.That(survivors.Select(p => p.Category), Is.EquivalentTo(new[] { "Y" }));
    }
}
