using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Relational.Repository;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Read;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.PostgreSql.Tests;

/// <summary>
///     Exercises the native PostgreSQL provider against a real server: atomic batched commits (generated keys
///     read back), real transactions with read-your-writes, the pushdown engine (native <c>OR</c>/<c>!=</c>/
///     <c>NULL</c>, gated residual), native paging and keyset continuation, computed updates including arrays,
///     global query filters and <c>Explain</c>.
/// </summary>
[TestFixture]
public sealed class PostgreSqlRepositoryTests : PostgreSqlIntegrationTest
{
    private static DateTime Utc(int hour) => new(2026, 1, 1, hour, 0, 0, DateTimeKind.Utc);

    private static SaleOrder NewOrder(string customer, decimal total = 0m, string? status = null,
        int quantity = 0, List<string>? tags = null) => new()
    {
        Id = Guid.NewGuid(),
        Customer = customer,
        Total = total,
        Status = status,
        Quantity = quantity,
        Tags = tags ?? [],
        CreatedAt = Utc(0),
    };

    // ---------------------------------------------------------------- writes + roundtrip

    [Test]
    public async Task Add_commit_then_get_round_trips_every_column()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        var order = NewOrder("alice", 125.50m, status: null, quantity: 3, tags: ["vip", "early"]);
        order.CreatedAt = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        await repo.AddAsync(order);
        var affected = await Uow(db).CommitAsync();

        Assert.That(affected, Is.EqualTo(1));
        var loaded = await repo.GetAsync(order.Id);
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Customer, Is.EqualTo("alice"));
        Assert.That(loaded.Total, Is.EqualTo(125.50m));
        Assert.That(loaded.Status, Is.Null);
        Assert.That(loaded.Tags, Is.EqualTo(new[] { "vip", "early" }));
        Assert.That(loaded.CreatedAt, Is.EqualTo(order.CreatedAt));
    }

    [Test]
    public async Task Generated_keys_are_read_back_into_the_entities()
    {
        using var db = await NewSchemaAsync();
        var repo = TicketRepo(db);
        var first = new Ticket { Label = "one" };
        var second = new Ticket { Label = "two" };

        await repo.AddAsync(first);
        await repo.AddAsync(second);
        await Uow(db).CommitAsync();

        Assert.That(first.Id, Is.GreaterThan(0), "RETURNING backfills the identity");
        Assert.That(second.Id, Is.GreaterThan(first.Id));
        Assert.That((await repo.GetAsync(second.Id))!.Label, Is.EqualTo("two"));
    }

    [Test]
    public async Task Staged_writes_are_invisible_until_commit_and_the_flush_is_atomic()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        var order = NewOrder("bob");

        await repo.AddAsync(order);
        Assert.That(await repo.GetAsync(order.Id), Is.Null, "staged, not yet flushed");
        await Uow(db).CommitAsync();
        Assert.That(await repo.GetAsync(order.Id), Is.Not.Null, "flushed on commit");

        // A duplicate key in the middle of a flush rolls the whole flush back — the commit is atomic.
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

        await uow.BeginTransactionAsync();
        await repo.AddAsync(NewOrder("durable"));
        await uow.CommitAsync();
        await uow.CommitTransactionAsync();
        Assert.That(await repo.CountAsync(), Is.EqualTo(1), "the committed transaction is durable");
    }

    [Test]
    public async Task Modify_and_remove_flush_by_key()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        var order = NewOrder("mutable", 10m);
        await Seed(db, order);

        order.Total = 99.99m;
        await repo.ModifyAsync(order);
        await Uow(db).CommitAsync();
        Assert.That((await repo.GetAsync(order.Id))!.Total, Is.EqualTo(99.99m));

        await repo.RemoveAsync(order);
        await Uow(db).CommitAsync();
        Assert.That(await repo.GetAsync(order.Id), Is.Null);
    }

    // ---------------------------------------------------------------- native pushdown (no opt-ins)

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

        // C# semantics: != matches the NULL status row too — no opt-in, everything server-side.
        Assert.That(found.Select(x => x.Customer), Is.EquivalentTo(new[] { "alice", "carol" }));
    }

    [Test]
    public async Task Contains_with_a_null_in_the_list_matches_null_rows()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        await Seed(db, NewOrder("a", status: "open"), NewOrder("b", status: "closed"), NewOrder("c", status: null));

        var statuses = new[] { "open", null };
        var found = await repo.GetFilteredAsync(x => statuses.Contains(x.Status));

        Assert.That(found.Select(x => x.Customer), Is.EquivalentTo(new[] { "a", "c" }));
    }

    [Test]
    public async Task Array_membership_pushes_down_as_any()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        await Seed(db, NewOrder("tagged", tags: ["vip"]), NewOrder("plain"));

        var found = await repo.GetFilteredAsync(x => x.Tags.Contains("vip"));

        Assert.That(found.Single().Customer, Is.EqualTo("tagged"));
    }

    [Test]
    public async Task String_functions_push_down_as_like_with_escaping()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        await Seed(db, NewOrder("alpha"), NewOrder("beta"), NewOrder("100% cotton"));

        // No opt-in: StartsWith/EndsWith/Contains are native LIKE now.
        Assert.That((await repo.GetFilteredAsync(x => x.Customer.StartsWith("al"))).Single().Customer, Is.EqualTo("alpha"));
        Assert.That((await repo.GetFilteredAsync(x => x.Customer.EndsWith("ton"))).Single().Customer, Is.EqualTo("100% cotton"));
        Assert.That((await repo.GetFilteredAsync(x => x.Customer.Contains("%"))).Single().Customer, Is.EqualTo("100% cotton"),
            "the wildcard in the value is escaped, not interpreted");
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
    }

    [Test]
    public async Task Min_max_and_average_push_down_without_truncation()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        await Seed(db, NewOrder("a", 10m, quantity: 1), NewOrder("a", 20m, quantity: 2), NewOrder("a", 40m, quantity: 3));

        var aggregates = (IAggregateReadRepository<SaleOrder>)repo;
        var scope = new QueryOptions<SaleOrder>().Where(x => x.Customer == "a");

        Assert.That(await aggregates.MinAsync(x => x.Total, scope), Is.EqualTo(10m));
        Assert.That(await aggregates.MaxAsync(x => x.Total, scope), Is.EqualTo(40m));
        Assert.That(await aggregates.AverageAsync(x => x.Quantity, scope), Is.EqualTo(2d),
            "the integer column is cast before averaging");
    }

    [Test]
    public async Task From_sql_escape_hatch_materializes_by_name()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        await Seed(db, NewOrder("raw", 42m), NewOrder("other", 1m));

        var rows = await ((RelationalReadRepository<SaleOrder, Guid>)repo).FromSqlAsync(
            "SELECT o.* FROM \"sale_orders\" o WHERE o.\"customer\" = @p0", ["raw"]);

        Assert.That(rows.Single().Total, Is.EqualTo(42m));
    }

    [Test]
    public async Task Includes_load_reference_and_collection_navigations()
    {
        using var db = await NewSchemaAsync();
        var buyers = db.Resolve<IAsyncRepository<Buyer, Guid>>();
        var items = db.Resolve<IAsyncRepository<OrderItem, Guid>>();
        var repo = OrderRepo(db);

        var buyer = new Buyer { Id = Guid.NewGuid(), Name = "acme" };
        await buyers.AddAsync(buyer);
        var order = NewOrder("with-navs");
        order.BuyerId = buyer.Id;
        await repo.AddAsync(order);
        await items.AddAsync(new OrderItem { Id = Guid.NewGuid(), SaleOrderId = order.Id, Product = "kb" });
        await items.AddAsync(new OrderItem { Id = Guid.NewGuid(), SaleOrderId = order.Id, Product = "mouse" });
        await Uow(db).CommitAsync();

        var loaded = await repo.GetAsync(order.Id,
            new QueryOptions<SaleOrder>().Include(nameof(SaleOrder.Buyer), nameof(SaleOrder.Items)));

        Assert.That(loaded!.Buyer, Is.Not.Null, "the reference navigation loaded");
        Assert.That(loaded.Buyer!.Name, Is.EqualTo("acme"));
        Assert.That(loaded.Items.Select(i => i.Product), Is.EquivalentTo(new[] { "kb", "mouse" }), "the collection navigation loaded");
    }

    // ---------------------------------------------------------------- sorting, paging, streaming, aggregates

    [Test]
    public async Task Sorting_and_offset_paging_are_native()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        await Seed(db, NewOrder("a", 30m), NewOrder("b", 10m), NewOrder("c", 20m), NewOrder("d", 40m));

        var page = await repo.GetPagedAsync(PageRequest.Of(2, 2),
            new QueryOptions<SaleOrder>().OrderBy(x => x.Total));

        Assert.That(page.TotalCount, Is.EqualTo(4));
        Assert.That(page.Items.Select(x => x.Total), Is.EqualTo(new[] { 30m, 40m }), "second page in Total order");
    }

    [Test]
    public async Task Keyset_continuation_walks_to_exhaustion()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        await Seed(db, NewOrder("a"), NewOrder("b"), NewOrder("c"), NewOrder("d"), NewOrder("e"));

        var pager = (IContinuationReadRepository<SaleOrder>)repo;
        var seen = new List<Guid>();
        string? token = null;
        var pages = 0;

        do
        {
            var page = await pager.GetPageAsync(2, token);
            Assert.That(page.Items, Has.Count.LessThanOrEqualTo(2));
            seen.AddRange(page.Items.Select(x => x.Id));
            token = page.ContinuationToken;
            pages++;
        } while (token is not null && pages < 10);

        Assert.That(seen, Has.Count.EqualTo(5));
        Assert.That(seen, Is.Unique);
        Assert.That(pages, Is.GreaterThanOrEqualTo(3));
    }

    [Test]
    public async Task Get_stream_yields_every_matching_row()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        await Seed(db, NewOrder("x", 1m), NewOrder("x", 2m), NewOrder("y", 3m));

        var seen = new List<SaleOrder>();
        await foreach (var order in ((IStreamingReadRepository<SaleOrder>)repo)
                           .GetStreamAsync(new QueryOptions<SaleOrder>().Where(x => x.Customer == "x")))
        {
            seen.Add(order);
        }

        Assert.That(seen, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Count_sum_and_projection_push_down()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        await Seed(db, NewOrder("a", 10.5m, quantity: 1), NewOrder("a", 20.25m, quantity: 2), NewOrder("b", 99m));

        var scope = new QueryOptions<SaleOrder>().Where(x => x.Customer == "a");
        Assert.That(await repo.CountAsync(scope), Is.EqualTo(2));
        Assert.That(await repo.SumAsync(x => x.Total, scope), Is.EqualTo(30.75m));
        Assert.That(await repo.SumAsync(x => x.Quantity, scope), Is.EqualTo(3));

        var projected = await repo.GetMappedAsync(x => new { x.Customer, x.Total }, scope);
        Assert.That(projected.Sum(x => x.Total), Is.EqualTo(30.75m));
    }

    // ---------------------------------------------------------------- computed set-based updates

    [Test]
    public async Task Update_many_applies_computed_shapes_atomically_with_real_counts()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        var order = NewOrder("calc", 10m, quantity: 4, tags: ["old", "keep"]);
        await Seed(db, order, NewOrder("other", 1m));

        var updated = await repo.UpdateManyAsync(x => x.Id == order.Id,
            x => new SaleOrder { Total = x.Total + 5m, Quantity = x.Quantity * 2, Status = "recalculated" });
        Assert.That(updated, Is.EqualTo(1), "the real affected-row count");

        await repo.UpdateManyAsync(x => x.Id == order.Id, x => new SaleOrder { Tags = x.Tags.Append("vip").ToList() });
        var gone = new[] { "old" };
        await repo.UpdateManyAsync(x => x.Id == order.Id, x => new SaleOrder { Tags = x.Tags.Except(gone).ToList() });

        var loaded = (await repo.GetAsync(order.Id))!;
        Assert.That(loaded.Total, Is.EqualTo(15m));
        Assert.That(loaded.Quantity, Is.EqualTo(8));
        Assert.That(loaded.Status, Is.EqualTo("recalculated"));
        Assert.That(loaded.Tags, Is.EqualTo(new[] { "keep", "vip" }), "array append and remove on the server");
    }

    [Test]
    public async Task Delete_many_returns_the_real_count()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        await Seed(db, NewOrder("doomed", 1m), NewOrder("doomed", 2m), NewOrder("kept", 3m));

        Assert.That(await repo.DeleteManyAsync(x => x.Customer == "doomed"), Is.EqualTo(2));
        Assert.That(await repo.CountAsync(), Is.EqualTo(1));
    }

    // ---------------------------------------------------------------- global query filters

    [Test]
    public async Task Global_filter_scopes_reads_and_writes_and_ignoring_opts_out()
    {
        using var db = await NewSchemaAsync(services =>
            services.AddSingleton(new QueryFilters().For<SaleOrder>(x => x.Customer == "tenant")));
        var repo = OrderRepo(db);
        await Seed(db, NewOrder("tenant", 1m), NewOrder("tenant", 2m), NewOrder("other", 3m));

        Assert.That(await repo.CountAsync(), Is.EqualTo(2), "reads are scoped");
        Assert.That(await repo.CountAsync(new QueryOptions<SaleOrder>().IgnoringQueryFilters()), Is.EqualTo(3));

        Assert.That(await repo.DeleteManyAsync(x => x.Total > 0m), Is.EqualTo(2), "set-based writes stay scoped");
        var survivors = await repo.GetAllAsync(new QueryOptions<SaleOrder>().IgnoringQueryFilters());
        Assert.That(survivors.Select(x => x.Customer), Is.EquivalentTo(new[] { "other" }));
    }

    // ---------------------------------------------------------------- explain

    [Test]
    public async Task Explain_reports_the_sql_and_the_gates()
    {
        using var db = await NewSchemaAsync();
        var explainable = (IExplainableRepository<SaleOrder>)OrderRepo(db);

        var pushed = explainable.Explain(new QueryOptions<SaleOrder>().Where(x => x.Customer == "a" || x.Total > 1m));
        Assert.That(pushed.Provider, Is.EqualTo("postgresql"));
        Assert.That(pushed.Statement, Does.Contain("\"customer\"").And.Contain("OR"));
        Assert.That(pushed.ClientEvaluation, Is.False);

        var residual = explainable.Explain(new QueryOptions<SaleOrder>().Where(x => x.Customer.Length > 3));
        Assert.That(residual.ClientEvaluation, Is.True);
        Assert.That(residual.Notes, Has.Some.Contains("AllowClientEvaluation"));
    }
}
