using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>
///     Covers <c>QueryOptions.Include</c> — navigation eager-loading rendered as a server-side <c>$lookup</c> on the
///     aggregation pipeline — against a real MongoDB cluster, for both reference and collection navigations.
/// </summary>
[TestFixture]
public sealed class MongoIncludeTests : MongoIntegrationTest
{
    private static Task SeedCustomers(MongoTestDatabase db, params Customer[] customers) =>
        db.Database.GetCollection<Customer>("Customer").InsertManyAsync(customers);

    private static Task SeedOrders(MongoTestDatabase db, params Order[] orders) =>
        db.Database.GetCollection<Order>("Order").InsertManyAsync(orders);

    [Test]
    public async Task Include_reference_navigation_populates_it_via_lookup()
    {
        using var db = NewDatabase();
        var customer = Customer.New("Acme");
        await SeedCustomers(db, customer);
        var order = Order.New(customer.Id, 100);
        await SeedOrders(db, order);

        var options = new QueryOptions<Order>().Where(o => o.Id == order.Id).Include(nameof(Order.Customer));
        var loaded = (await db.Resolve<IAsyncRepository<Order, string>>().GetAllAsync(options)).Single();

        Assert.That(loaded.Customer, Is.Not.Null);
        Assert.That(loaded.Customer!.Name, Is.EqualTo("Acme"));
        Assert.That(loaded.Amount, Is.EqualTo(100), "the entity's own fields survive the join projection");
    }

    [Test]
    public async Task Include_collection_navigation_populates_every_match()
    {
        using var db = NewDatabase();
        var customer = Customer.New("Globex");
        await SeedCustomers(db, customer);
        await SeedOrders(db,
            Order.New(customer.Id, 10),
            Order.New(customer.Id, 20),
            Order.New("someone-else", 30));

        var options = new QueryOptions<Customer>().Where(c => c.Id == customer.Id).Include(nameof(Customer.Orders));
        var loaded = (await db.Resolve<IAsyncRepository<Customer, string>>().GetAllAsync(options)).Single();

        Assert.That(loaded.Orders, Is.Not.Null);
        Assert.That(loaded.Orders!.Select(o => o.Amount), Is.EquivalentTo(new[] { 10, 20 }));
    }

    [Test]
    public async Task Without_include_the_navigation_stays_null()
    {
        using var db = NewDatabase();
        var customer = Customer.New("Initech");
        await SeedCustomers(db, customer);
        var order = Order.New(customer.Id, 5);
        await SeedOrders(db, order);

        var loaded = await db.Resolve<IAsyncRepository<Order, string>>().GetAsync(order.Id);

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Customer, Is.Null);
    }

    [Test]
    public async Task Include_keeps_entities_that_have_no_match()
    {
        using var db = NewDatabase();
        var order = Order.New("missing-customer", 7);
        await SeedOrders(db, order);

        var options = new QueryOptions<Order>().Where(o => o.Id == order.Id).Include(nameof(Order.Customer));
        var loaded = (await db.Resolve<IAsyncRepository<Order, string>>().GetAllAsync(options)).Single();

        Assert.That(loaded.Amount, Is.EqualTo(7), "a left join keeps the order even with no customer");
        Assert.That(loaded.Customer, Is.Null);
    }
}
