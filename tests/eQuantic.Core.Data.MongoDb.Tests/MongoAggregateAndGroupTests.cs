using eQuantic.Core.Data.Query;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Read;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>
///     Exercises the aggregate, grouped and union surfaces against a real MongoDB: single-group
///     <c>$min</c>/<c>$max</c>/<c>$avg</c>, typed <c>GroupBy</c> as a server-side <c>$group</c> with a
///     <c>$match</c> HAVING, and typed unions as <c>$unionWith</c> pipelines with per-branch filters and tags.
/// </summary>
[TestFixture]
public sealed class MongoAggregateAndGroupTests : MongoIntegrationTest
{
    [Test]
    public async Task Min_max_and_average_run_as_single_group_aggregations()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("a", "tools", 1, 10m), Product.New("b", "tools", 3, 40m));
        var aggregates = (IAggregateReadRepository<Product>)AsyncRepo(db);
        var scope = new QueryOptions<Product>().Where(x => x.Category == "tools");

        Assert.That(await aggregates.MinAsync(x => x.Price, scope), Is.EqualTo(10m));
        Assert.That(await aggregates.MaxAsync(x => x.Price, scope), Is.EqualTo(40m));
        Assert.That(await aggregates.AverageAsync(x => x.Quantity, scope), Is.EqualTo(2d),
            "the integer member converts to double before averaging");

        var none = new QueryOptions<Product>().Where(x => x.Category == "empty");
        Assert.That(await aggregates.MinAsync(x => x.Price, none), Is.EqualTo(0m), "an empty match yields default");
        Assert.That(await aggregates.AverageAsync(x => x.Quantity, none), Is.EqualTo(0d));
    }

    [Test]
    public async Task Group_by_runs_as_a_server_side_group_with_having()
    {
        using var db = NewDatabase();
        await Seed(db,
            Product.New("p1", "tools", 1, 10m), Product.New("p2", "tools", 3, 20m),
            Product.New("p3", "toys", 2, 40m), Product.New("p4", "food", 1, 5m));
        var grouped = (IGroupedReadRepository<Product>)AsyncRepo(db);

        var groups = (await grouped.GroupByAsync(x => x.Category,
                g => new { Category = g.Key, Items = g.Count(), Value = g.Sum(x => x.Price), Mean = g.Average(x => x.Quantity) }))
            .OrderBy(x => x.Category).ToList();
        Assert.That(groups.Select(x => (x.Category, x.Items, x.Value, x.Mean)),
            Is.EqualTo(new[] { ("food", 1, 5m, 1d), ("tools", 2, 30m, 2d), ("toys", 1, 40m, 2d) }));

        var big = await grouped.GroupByAsync(x => x.Category, g => new { g.Key, Value = g.Sum(x => x.Price) },
            having: g => g.Sum(x => x.Price) > 15m && g.Count() >= 2);
        Assert.That(big.Single().Key, Is.EqualTo("tools"), "the $match after $group filtered the groups");
    }

    [Test]
    public async Task Group_by_composite_key_with_the_filter_before_grouping()
    {
        using var db = NewDatabase();
        await Seed(db,
            Product.New("hammer", "tools", 1, 10m), Product.New("hammer", "tools", 2, 20m),
            Product.New("saw", "tools", 0, 40m), Product.New("ball", "toys", 1, 5m));
        var grouped = (IGroupedReadRepository<Product>)AsyncRepo(db);

        var members = (await grouped.GroupByAsync(x => new { x.Category, x.Name },
                g => new { g.Key.Category, g.Key.Name, Total = g.Sum(x => x.Price) },
                options: new QueryOptions<Product>().Where(x => x.Quantity > 0)))
            .OrderBy(x => x.Category).ThenBy(x => x.Name).ToList();

        Assert.That(members.Select(x => (x.Category, x.Name, x.Total)),
            Is.EqualTo(new[] { ("tools", "hammer", 30m), ("toys", "ball", 5m) }),
            "the filter applied before grouping and the composite key projected member by member");
    }

    [Test]
    public async Task Union_all_combines_collections_with_tags_and_distinct_dedupes()
    {
        using var db = NewDatabase();
        await Seed(db, Product.New("widget", "tools", 1, 10m), Product.New("gear", "toys", 1, 5m));
        await db.Database.GetCollection<Customer>("Customer")
            .InsertManyAsync([new Customer { Id = "c1", Name = "acme" }]);

        var rows = await Uow(db).UnionAsync(UnionQuery.All(
                Union.Of<Product>().Where(x => x.Category == "tools").Select(x => new { x.Name, Origin = "product" }),
                Union.Of<Customer>().Select(x => new { x.Name, Origin = "customer" }))
            .OrderBy(row => row.Name));

        Assert.That(rows.Select(x => (x.Name, x.Origin)),
            Is.EqualTo(new[] { ("acme", "customer"), ("widget", "product") }),
            "$unionWith combined the collections, tagged per branch, ordered on the server");

        var branch = Union.Of<Product>().Select(x => new { x.Category });
        var again = Union.Of<Product>().Select(x => new { x.Category });
        Assert.That(await Uow(db).UnionAsync(UnionQuery.All(branch, again)), Has.Count.EqualTo(4),
            "UNION ALL keeps every row");
        Assert.That(await Uow(db).UnionAsync(UnionQuery.Distinct(branch, again)), Has.Count.EqualTo(2),
            "Distinct deduplicates the combined shape with $group");
    }

    [Test]
    public async Task Union_global_filters_scope_each_branch_with_opt_out()
    {
        using var db = NewDatabase(services =>
            services.AddSingleton(new QueryFilters().For<Product>(x => x.Category != "hidden")));
        await Seed(db, Product.New("h", "hidden", 1, 1m), Product.New("v", "visible", 1, 2m));

        var rows = await Uow(db).UnionAsync(UnionQuery.All(
            Union.Of<Product>().Select(x => new { x.Name, Origin = "scoped" }),
            Union.Of<Product>().IgnoringQueryFilters().Select(x => new { x.Name, Origin = "all" })));

        Assert.That(rows.Count(x => x.Origin == "scoped"), Is.EqualTo(1), "the global filter scoped the branch");
        Assert.That(rows.Count(x => x.Origin == "all"), Is.EqualTo(2), "IgnoringQueryFilters opted the branch out");
    }

    [Test]
    public async Task Grouped_and_union_contracts_reject_unsupported_shapes()
    {
        using var db = NewDatabase();
        var grouped = (IGroupedReadRepository<Product>)AsyncRepo(db);

        Assert.That(async () => await grouped.GroupByAsync(x => x.Category, g => new { Odd = g.Count() * 2 }),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("Supported shapes"),
            "the grouped contract is uniform across providers");
        Assert.That(async () => await Uow(db).UnionAsync(UnionQuery.All(
                Union.Of<Product>().Select(x => new { Doubled = x.Quantity * 2 }),
                Union.Of<Product>().Select(x => new { Doubled = x.Quantity * 2 }))),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("entity member or a constant"),
            "the union contract is uniform across providers");
    }
}
