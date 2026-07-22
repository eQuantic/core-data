using eQuantic.Core.Data.Query;
using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Read;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.SqlServer.Tests;

/// <summary>
///     Exercises the native SQL Server provider against a real server: atomic batched commits with
///     <c>OUTPUT INSERTED</c> identity backfill, real transactions with read-your-writes, the pushdown engine
///     (native <c>OR</c>/<c>!=</c>/<c>NULL</c>, gated residual), <c>OFFSET/FETCH</c> paging (with the implicit
///     key order limits demand), keyset continuation, computed updates, global query filters and <c>Explain</c>.
/// </summary>
[TestFixture]
public sealed class SqlServerRepositoryTests : SqlServerIntegrationTest
{
    private static SaleOrder NewOrder(string customer, decimal total = 0m, string? status = null, int quantity = 0) => new()
    {
        Id = Guid.NewGuid(),
        Customer = customer,
        Total = total,
        Status = status,
        Quantity = quantity,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    [Test]
    public async Task Add_commit_then_get_round_trips_every_column()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        var order = NewOrder("alice", 125.5m, status: null, quantity: 3);

        await repo.AddAsync(order);
        Assert.That(await Uow(db).CommitAsync(), Is.EqualTo(1));

        var loaded = await repo.GetAsync(order.Id);
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Customer, Is.EqualTo("alice"));
        Assert.That(loaded.Total, Is.EqualTo(125.5m));
        Assert.That(loaded.Status, Is.Null);
    }

    [Test]
    public async Task Identity_keys_are_read_back_through_output_inserted()
    {
        using var db = await NewSchemaAsync();
        var repo = TicketRepo(db);
        var first = new Ticket { Label = "one" };
        var second = new Ticket { Label = "two" };

        await repo.AddAsync(first);
        await repo.AddAsync(second);
        await Uow(db).CommitAsync();

        Assert.That(first.Id, Is.GreaterThan(0), "OUTPUT INSERTED backfills the identity");
        Assert.That(second.Id, Is.GreaterThan(first.Id));
        Assert.That((await repo.GetAsync(second.Id))!.Label, Is.EqualTo("two"));
    }

    [Test]
    public async Task The_flush_is_atomic_and_transactions_read_their_writes()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        var good = NewOrder("carol");
        var duplicate = NewOrder("dave");
        duplicate.Id = good.Id;

        await repo.AddAsync(good);
        await repo.AddAsync(duplicate);
        Assert.That(async () => await Uow(db).CommitAsync(), Throws.Exception);
        Assert.That(await repo.GetAsync(good.Id), Is.Null, "the first insert rolled back with the failed one");

        var uow = Uow(db);
        var order = NewOrder("tx");
        await uow.BeginTransactionAsync();
        await repo.AddAsync(order);
        await uow.CommitAsync();
        Assert.That(await repo.GetAsync(order.Id), Is.Not.Null, "the transaction's own read sees the flushed write");
        await uow.RollbackTransactionAsync();
        Assert.That(await repo.GetAsync(order.Id), Is.Null, "the rollback discarded it");
    }

    [Test]
    public async Task Or_not_equal_and_null_are_native_sql()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        await Seed(db,
            NewOrder("alice", 50m, "open"),
            NewOrder("bob", 200m, "closed"),
            NewOrder("carol", 300m, status: null));

        var found = await repo.GetFilteredAsync(x => x.Customer == "alice" || (x.Total > 100m && x.Status != "closed"));

        Assert.That(found.Select(x => x.Customer), Is.EquivalentTo(new[] { "alice", "carol" }));
    }

    [Test]
    public async Task Limits_carry_the_implicit_order_offset_fetch_demands()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        await Seed(db, NewOrder("a", 30m), NewOrder("b", 10m), NewOrder("c", 20m), NewOrder("d", 40m));

        // GetFirst pushes a bare limit — the dialect demands ORDER BY, the engine adds the key order.
        Assert.That(await repo.GetFirstAsync(new QueryOptions<SaleOrder>().Where(x => x.Total > 0m)), Is.Not.Null);

        var page = await repo.GetPagedAsync(PageRequest.Of(2, 2), new QueryOptions<SaleOrder>().OrderBy(x => x.Total));
        Assert.That(page.Items.Select(x => x.Total), Is.EqualTo(new[] { 30m, 40m }), "OFFSET/FETCH second page");

        var pager = (IContinuationReadRepository<SaleOrder>)repo;
        var seen = new List<Guid>();
        string? token = null;
        do
        {
            var next = await pager.GetPageAsync(3, token);
            seen.AddRange(next.Items.Select(x => x.Id));
            token = next.ContinuationToken;
        } while (token is not null && seen.Count < 20);

        Assert.That(seen, Has.Count.EqualTo(4));
        Assert.That(seen, Is.Unique);
    }

    [Test]
    public async Task Arbitrary_predicates_are_gated_residual()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        await Seed(db, NewOrder("alpha"), NewOrder("beta"));

        Assert.That(async () => await repo.GetFilteredAsync(x => x.Customer.Length > 4),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("AllowClientEvaluation"));

        var found = await repo.GetFilteredAsync(x => x.Customer.Length > 4,
            new QueryOptions<SaleOrder>().AllowClientEvaluation());
        Assert.That(found.Single().Customer, Is.EqualTo("alpha"));

        // StartsWith is native LIKE now — no opt-in.
        Assert.That((await repo.GetFilteredAsync(x => x.Customer.StartsWith("be"))).Single().Customer, Is.EqualTo("beta"));
    }


    [Test]
    public async Task Date_functions_push_down()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        await Seed(db, NewOrder("dated"));

        Assert.That((await repo.GetFilteredAsync(x => Db.Year(x.CreatedAt) == 2026)).Count(), Is.EqualTo(1));
        Assert.That((await repo.GetFilteredAsync(x => x.Customer.ToLower() == "dated")).Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task Min_max_and_average_push_down()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        await Seed(db, NewOrder("agg", 10m, quantity: 1), NewOrder("agg", 40m, quantity: 3));

        var aggregates = (IAggregateReadRepository<SaleOrder>)repo;
        var scope = new QueryOptions<SaleOrder>().Where(x => x.Customer == "agg");

        Assert.That(await aggregates.MinAsync(x => x.Total, scope), Is.EqualTo(10m));
        Assert.That(await aggregates.MaxAsync(x => x.Total, scope), Is.EqualTo(40m));
        Assert.That(await aggregates.AverageAsync(x => x.Quantity, scope), Is.EqualTo(2d));
    }

    [Test]
    public async Task Typed_group_by_is_a_native_group_by()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        await Seed(db,
            NewOrder("a", 10m, "open", quantity: 1), NewOrder("a", 20m, "closed", quantity: 3),
            NewOrder("b", 40m, "open", quantity: 2));

        var groups = (await ((IGroupedReadRepository<SaleOrder>)repo).GroupByAsync(
                x => new { x.Customer, x.Status },
                g => new { g.Key.Customer, g.Key.Status, Revenue = g.Sum(x => x.Total), Orders = g.Count() }))
            .OrderBy(x => x.Customer).ThenBy(x => x.Status).ToList();

        Assert.That(groups.Select(x => (x.Customer, x.Status, x.Revenue, x.Orders)),
            Is.EqualTo(new[] { ("a", "closed", 20m, 1), ("a", "open", 10m, 1), ("b", "open", 40m, 1) }));

        var big = await ((IGroupedReadRepository<SaleOrder>)repo).GroupByAsync(
            x => x.Customer, g => new { g.Key, Revenue = g.Sum(x => x.Total) },
            having: g => g.Sum(x => x.Total) > 35m);
        Assert.That(big.Single().Key, Is.EqualTo("b"), "HAVING filtered the groups on the server");
    }

    [Test]
    public async Task Typed_union_pages_the_combined_result_with_offset_fetch()
    {
        using var db = await NewSchemaAsync();
        await Seed(db, NewOrder("a", 10m, "open"), NewOrder("b", 20m, "closed"), NewOrder("c", 30m, "open"));

        var union = UnionQuery.All(
                Union.Of<SaleOrder>().Where(x => x.Status == "open").Select(x => new { x.Customer, Origin = "open" }),
                Union.Of<SaleOrder>().Where(x => x.Status == "closed").Select(x => new { x.Customer, Origin = "closed" }))
            .OrderBy(row => row.Customer).Take(2);

        var rows = await Uow(db).UnionAsync(union);
        Assert.That(rows.Select(x => (x.Customer, x.Origin)),
            Is.EqualTo(new[] { ("a", "open"), ("b", "closed") }), "OFFSET/FETCH paged the union");

        Assert.That(async () => await Uow(db).UnionAsync(UnionQuery.All(
                Union.Of<SaleOrder>().Select(x => new { x.Customer }),
                Union.Of<SaleOrder>().Select(x => new { x.Customer })).Take(1)),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("ORDER BY"),
            "paging a union without ordering is rejected on this dialect");
    }

    [Test]
    public async Task Update_many_applies_computed_shapes_with_real_counts()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        var order = NewOrder("calc", 10m, quantity: 4);
        await Seed(db, order, NewOrder("other"));

        var updated = await repo.UpdateManyAsync(x => x.Id == order.Id,
            x => new SaleOrder { Total = x.Total + 5m, Quantity = x.Quantity * 2 });

        Assert.That(updated, Is.EqualTo(1));
        var loaded = (await repo.GetAsync(order.Id))!;
        Assert.That(loaded.Total, Is.EqualTo(15m));
        Assert.That(loaded.Quantity, Is.EqualTo(8));
    }

    [Test]
    public async Task Global_filter_scopes_reads_and_writes_and_ignoring_opts_out()
    {
        using var db = await NewSchemaAsync(services =>
            services.AddSingleton(new QueryFilters().For<SaleOrder>(x => x.Customer == "tenant")));
        var repo = OrderRepo(db);
        await Seed(db, NewOrder("tenant", 1m), NewOrder("other", 2m));

        Assert.That(await repo.CountAsync(), Is.EqualTo(1));
        Assert.That(await repo.CountAsync(new QueryOptions<SaleOrder>().IgnoringQueryFilters()), Is.EqualTo(2));
        Assert.That(await repo.DeleteManyAsync(x => x.Total > 0m), Is.EqualTo(1), "writes stay scoped");
    }

    [Test]
    public async Task Explain_reports_the_sql()
    {
        using var db = await NewSchemaAsync();
        var plan = ((IExplainableRepository<SaleOrder>)OrderRepo(db))
            .Explain(new QueryOptions<SaleOrder>().Where(x => x.Customer == "a" || x.Total > 1m));

        Assert.That(plan.Provider, Is.EqualTo("mssql"));
        Assert.That(plan.Statement, Does.Contain("[customer]").And.Contain("OR"));
        Assert.That(plan.ClientEvaluation, Is.False);
    }
}
