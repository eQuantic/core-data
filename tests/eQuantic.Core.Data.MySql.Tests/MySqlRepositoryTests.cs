using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Read;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.MySql.Tests;

/// <summary>
///     Exercises the native MySQL provider against a real server: atomic batched commits, real transactions with
///     read-your-writes, the pushdown engine (native <c>OR</c>/<c>!=</c>/<c>NULL</c>, gated residual), native
///     paging plus keyset continuation, computed updates, global query filters, <c>Explain</c> — and the honest
///     rejection of generated-key backfill (MySQL has no <c>RETURNING</c>).
/// </summary>
[TestFixture]
public sealed class MySqlRepositoryTests : MySqlIntegrationTest
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
    public async Task Generated_key_backfill_is_rejected_with_guidance()
    {
        using var db = await NewSchemaAsync();
        var repo = db.Resolve<IAsyncRepository<Ticket, long>>();

        await repo.AddAsync(new Ticket { Label = "one" });
        Assert.That(async () => await Uow(db).CommitAsync(),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("client-generated"));
    }

    [Test]
    public async Task The_flush_is_atomic()
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
    }

    [Test]
    public async Task Transactions_span_commits_with_read_your_writes()
    {
        using var db = await NewSchemaAsync();
        var uow = Uow(db);
        var repo = OrderRepo(db);
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
    public async Task Sorting_offset_paging_keyset_and_aggregates_are_native()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        await Seed(db, NewOrder("a", 30m, quantity: 1), NewOrder("a", 10m, quantity: 2),
            NewOrder("a", 20m, quantity: 3), NewOrder("b", 40m));

        var page = await repo.GetPagedAsync(PageRequest.Of(2, 2), new QueryOptions<SaleOrder>().OrderBy(x => x.Total));
        Assert.That(page.Items.Select(x => x.Total), Is.EqualTo(new[] { 30m, 40m }));

        var scope = new QueryOptions<SaleOrder>().Where(x => x.Customer == "a");
        Assert.That(await repo.CountAsync(scope), Is.EqualTo(3));
        Assert.That(await repo.SumAsync(x => x.Quantity, scope), Is.EqualTo(6));

        var pager = (IContinuationReadRepository<SaleOrder>)repo;
        var seen = new List<Guid>();
        string? token = null;
        do
        {
            var next = await pager.GetPageAsync(2, token);
            seen.AddRange(next.Items.Select(x => x.Id));
            token = next.ContinuationToken;
        } while (token is not null && seen.Count < 20);

        Assert.That(seen, Has.Count.EqualTo(4));
        Assert.That(seen, Is.Unique);
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

        Assert.That(plan.Provider, Is.EqualTo("mysql"));
        Assert.That(plan.Statement, Does.Contain("`customer`").And.Contain("OR"));
        Assert.That(plan.ClientEvaluation, Is.False);
    }
}
