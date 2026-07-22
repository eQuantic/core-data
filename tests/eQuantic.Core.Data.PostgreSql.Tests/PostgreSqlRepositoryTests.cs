using eQuantic.Core.Data.Query;
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

    /// <summary>A developer marker with a mapped translation (see the functions test).</summary>
    public static string Reversed(string value) => new(value.Reverse().ToArray());

    /// <summary>A developer marker left unmapped — its real body runs in the gated residual.</summary>
    public static bool IsShort(string value) => value.Length < 5;

    [Test]
    public async Task Database_functions_translate_natively_and_extend()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        await Seed(db, NewOrder("Alpha"), NewOrder("beta"));

        // The standard set — no opt-ins, everything server-side.
        Assert.That((await repo.GetFilteredAsync(x => x.Customer.ToLower() == "alpha")).Single().Customer, Is.EqualTo("Alpha"));
        Assert.That((await repo.GetFilteredAsync(x => Db.Like(x.Customer, "_lpha"))).Single().Customer, Is.EqualTo("Alpha"),
            "Db.Like keeps the wildcards raw");
        Assert.That((await repo.GetFilteredAsync(x => Db.Year(x.CreatedAt) == 2026)).Count(), Is.EqualTo(2));

        // A developer-defined function: a marker with a real body plus a mapped translation.
        db.Resolve<SqlDialect>().Functions.Map("Reversed", (column, _) => $"REVERSE({column})");
        Assert.That((await repo.GetFilteredAsync(x => Reversed(x.Customer) == "ateb")).Single().Customer, Is.EqualTo("beta"));

        // An unmapped marker degrades to the gated residual, where its real body runs.
        Assert.That(async () => await repo.GetFilteredAsync(x => IsShort(x.Customer)),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("AllowClientEvaluation"));
        var shorts = await repo.GetFilteredAsync(x => IsShort(x.Customer), new QueryOptions<SaleOrder>().AllowClientEvaluation());
        Assert.That(shorts.Single().Customer, Is.EqualTo("beta"));
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

    // ---------------------------------------------------------------- typed GroupBy

    [Test]
    public async Task Group_by_renders_a_native_group_by_with_aggregates()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        await Seed(db,
            NewOrder("a", 10m, quantity: 1), NewOrder("a", 20m, quantity: 3),
            NewOrder("b", 40m, quantity: 2));

        var grouped = (IGroupedReadRepository<SaleOrder>)repo;
        var groups = (await grouped.GroupByAsync(x => x.Customer,
                g => new { Customer = g.Key, Orders = g.Count(), Revenue = g.Sum(x => x.Total), Mean = g.Average(x => x.Quantity) }))
            .OrderBy(x => x.Customer).ToList();

        Assert.That(groups.Select(x => (x.Customer, x.Orders, x.Revenue, x.Mean)),
            Is.EqualTo(new[] { ("a", 2, 30m, 2d), ("b", 1, 40m, 2d) }));
    }

    [Test]
    public async Task Group_by_filter_applies_before_grouping()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        await Seed(db, NewOrder("a", 10m, "open"), NewOrder("a", 20m, "closed"), NewOrder("b", 5m, "open"));

        var groups = (await ((IGroupedReadRepository<SaleOrder>)repo).GroupByAsync(
                x => x.Customer, g => new { g.Key, Total = g.Sum(x => x.Total) },
                options: new QueryOptions<SaleOrder>().Where(x => x.Status == "open")))
            .OrderBy(x => x.Key).ToList();

        Assert.That(groups.Select(x => (x.Key, x.Total)), Is.EqualTo(new[] { ("a", 10m), ("b", 5m) }),
            "the WHERE pushed before the GROUP BY");
    }

    [Test]
    public async Task Group_by_composite_key_projects_members_or_the_whole_key()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        await Seed(db,
            NewOrder("a", 10m, "open"), NewOrder("a", 20m, "open"),
            NewOrder("a", 40m, "closed"), NewOrder("b", 1m, "open"));
        var grouped = (IGroupedReadRepository<SaleOrder>)repo;

        var members = (await grouped.GroupByAsync(x => new { x.Customer, x.Status },
                g => new { g.Key.Customer, g.Key.Status, Subtotal = g.Sum(x => x.Total) }))
            .OrderBy(x => x.Customer).ThenBy(x => x.Status).ToList();
        Assert.That(members.Select(x => (x.Customer, x.Status, x.Subtotal)),
            Is.EqualTo(new[] { ("a", "closed", 40m), ("a", "open", 30m), ("b", "open", 1m) }));

        var whole = await grouped.GroupByAsync(x => new { x.Customer, x.Status },
            g => new { Bucket = g.Key, Rows = g.Count() });
        Assert.That(whole.Single(x => x.Bucket.Customer == "a" && x.Bucket.Status == "open").Rows, Is.EqualTo(2),
            "the composite key materializes back as the anonymous key");
    }

    [Test]
    public async Task Group_by_member_init_projects_into_a_named_type()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        await Seed(db, NewOrder("a", 10m), NewOrder("a", 30m), NewOrder("b", 7m));

        var summaries = (await ((IGroupedReadRepository<SaleOrder>)repo).GroupByAsync(
                x => x.Customer,
                g => new CustomerSummary { Customer = g.Key, Orders = g.Count(), Smallest = g.Min(x => x.Total) }))
            .OrderBy(x => x.Customer).ToList();

        Assert.That(summaries.Select(x => (x.Customer, x.Orders, x.Smallest)),
            Is.EqualTo(new[] { ("a", 2, 10m), ("b", 1, 7m) }));
    }

    [Test]
    public async Task Group_by_having_filters_groups_on_the_server()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        await Seed(db, NewOrder("a", 10m), NewOrder("a", 20m), NewOrder("b", 40m), NewOrder("c", 5m));
        var grouped = (IGroupedReadRepository<SaleOrder>)repo;

        var big = (await grouped.GroupByAsync(x => x.Customer, g => new { g.Key, Total = g.Sum(x => x.Total) },
                having: g => g.Sum(x => x.Total) > 15m))
            .OrderBy(x => x.Key).ToList();
        Assert.That(big.Select(x => (x.Key, x.Total)), Is.EqualTo(new[] { ("a", 30m), ("b", 40m) }));

        var busy = await grouped.GroupByAsync(x => x.Customer, g => new { g.Key, Orders = g.Count() },
            having: g => g.Sum(x => x.Total) > 15m && g.Count() >= 2);
        Assert.That(busy.Single().Key, Is.EqualTo("a"), "aggregates combine in HAVING");

        var named = await grouped.GroupByAsync(x => x.Customer, g => new { g.Key, Rows = g.Count() },
            having: g => g.Key != "a" && g.Count() >= 1);
        Assert.That(named.Select(x => x.Key), Is.EquivalentTo(new[] { "b", "c" }), "key members work in HAVING");
    }

    [Test]
    public async Task Group_by_residual_filter_degrades_to_gated_client_grouping()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        await Seed(db, NewOrder("alpha", 10m), NewOrder("alpha", 20m), NewOrder("bo", 40m));
        var grouped = (IGroupedReadRepository<SaleOrder>)repo;

        Assert.That(async () => await grouped.GroupByAsync(x => x.Customer, g => new { g.Key, Rows = g.Count() },
                options: new QueryOptions<SaleOrder>().Where(x => x.Customer.Length > 4)),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("AllowClientEvaluation"));

        var groups = await grouped.GroupByAsync(x => x.Customer, g => new { g.Key, Total = g.Sum(x => x.Total) },
            options: new QueryOptions<SaleOrder>().Where(x => x.Customer.Length > 4).AllowClientEvaluation());
        Assert.That(groups.Single(), Is.EqualTo(new { Key = "alpha", Total = 30m }),
            "the gated fallback groups the fetched rows with the selectors themselves");

        var kept = await grouped.GroupByAsync(x => x.Customer, g => new { g.Key, Total = g.Sum(x => x.Total) },
            having: g => g.Count() >= 2,
            options: new QueryOptions<SaleOrder>().Where(x => x.Customer.Length > 1).AllowClientEvaluation());
        Assert.That(kept.Single().Key, Is.EqualTo("alpha"), "the fallback applies HAVING to the client-side groups");
    }

    [Test]
    public async Task Group_by_rejects_unsupported_projections_and_sorting()
    {
        using var db = await NewSchemaAsync();
        var grouped = (IGroupedReadRepository<SaleOrder>)OrderRepo(db);

        Assert.That(async () => await grouped.GroupByAsync(x => x.Customer, g => new { Odd = g.Count() * 2 }),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("Supported shapes"));
        Assert.That(async () => await grouped.GroupByAsync(x => x.Customer, g => new { g.Key },
                options: new QueryOptions<SaleOrder>().OrderBy(x => x.Total)),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("Sorting"));
        Assert.That(async () => await grouped.GroupByAsync(x => x.Customer, g => new { g.Key },
                having: g => g.First().Total > 1m),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("HAVING"));
    }

    // ---------------------------------------------------------------- jsonb document columns

    [Test]
    public async Task Jsonb_dictionary_round_trips_and_filters_natively()
    {
        using var db = await NewSchemaAsync();
        var repo = OrderRepo(db);
        var tagged = NewOrder("doc", 1m);
        tagged.Attributes = new Dictionary<string, string> { ["tier"] = "gold", ["region"] = "emea" };
        await Seed(db, tagged, NewOrder("plain", 2m));

        var loaded = (await repo.GetAsync(tagged.Id))!;
        Assert.That(loaded.Attributes, Is.EqualTo(tagged.Attributes), "the dictionary round-trips through jsonb");

        var byKey = await repo.GetFilteredAsync(x => x.Attributes.ContainsKey("tier"));
        Assert.That(byKey.Single().Customer, Is.EqualTo("doc"), "ContainsKey pushed down as the jsonb ? operator");

        var byValue = await repo.GetFilteredAsync(x => x.Attributes["tier"] == "gold");
        Assert.That(byValue.Single().Customer, Is.EqualTo("doc"), "the indexer pushed down as jsonb ->>");

        Assert.That(await repo.CountAsync(new QueryOptions<SaleOrder>().Where(x => x.Attributes["tier"] == "silver")),
            Is.Zero, "value mismatches filter out on the server");
    }

    // ---------------------------------------------------------------- typed Union / UnionAll

    [Test]
    public async Task Union_all_combines_entities_into_a_common_shape_with_branch_tags()
    {
        using var db = await NewSchemaAsync();
        var buyers = db.Resolve<IAsyncRepository<Buyer, Guid>>();
        await buyers.AddAsync(new Buyer { Id = Guid.NewGuid(), Name = "zed" });
        await Seed(db, NewOrder("alice", 10m, "open"), NewOrder("bob", 5m, "closed"));

        var rows = await Uow(db).UnionAsync(UnionQuery.All(
                Union.Of<SaleOrder>().Where(x => x.Status == "open")
                    .Select(x => new { Name = x.Customer, Origin = "order" }),
                Union.Of<Buyer>().Select(x => new { x.Name, Origin = "buyer" }))
            .OrderBy(row => row.Name));

        Assert.That(rows.Select(x => (x.Name, x.Origin)),
            Is.EqualTo(new[] { ("alice", "order"), ("zed", "buyer") }),
            "two entities combined into one shape, tagged per branch, ordered on the store");
    }

    [Test]
    public async Task Union_distinct_collapses_duplicates_and_all_keeps_them()
    {
        using var db = await NewSchemaAsync();
        await Seed(db, NewOrder("dup", 10m, "open"), NewOrder("dup", 20m, "open"));

        var open = Union.Of<SaleOrder>().Where(x => x.Status == "open").Select(x => new { x.Customer });
        var every = Union.Of<SaleOrder>().Select(x => new { x.Customer });

        Assert.That(await Uow(db).UnionAsync(UnionQuery.All(open, every)), Has.Count.EqualTo(4),
            "UNION ALL keeps every row from every branch");
        Assert.That((await Uow(db).UnionAsync(UnionQuery.Distinct(open, every))).Single().Customer, Is.EqualTo("dup"),
            "UNION collapses duplicate projected rows on the store");
    }

    [Test]
    public async Task Union_orders_and_pages_the_combined_result()
    {
        using var db = await NewSchemaAsync();
        await Seed(db, NewOrder("a", 1m, "open"), NewOrder("b", 2m, "closed"), NewOrder("c", 3m, "open"), NewOrder("d", 4m, "closed"));

        var rows = await Uow(db).UnionAsync(UnionQuery.All(
                Union.Of<SaleOrder>().Where(x => x.Status == "open").Select(x => new UnionRow { Name = x.Customer, Origin = "open" }),
                Union.Of<SaleOrder>().Where(x => x.Status == "closed").Select(x => new UnionRow { Name = x.Customer, Origin = "closed" }))
            .OrderByDescending(row => row.Name).Take(2).Skip(1));

        Assert.That(rows.Select(x => (x.Name, x.Origin)), Is.EqualTo(new[] { ("c", "open"), ("b", "closed") }),
            "member-init shape, ordered descending, paged on the store");
    }

    [Test]
    public async Task Union_global_filters_scope_each_branch_with_per_branch_opt_out()
    {
        using var db = await NewSchemaAsync(services =>
            services.AddSingleton(new QueryFilters().For<SaleOrder>(x => x.Customer != "hidden")));
        await Seed(db, NewOrder("hidden", 1m), NewOrder("visible", 2m));

        var rows = await Uow(db).UnionAsync(UnionQuery.All(
            Union.Of<SaleOrder>().Select(x => new { x.Customer, Origin = "scoped" }),
            Union.Of<SaleOrder>().IgnoringQueryFilters().Select(x => new { x.Customer, Origin = "all" })));

        Assert.That(rows.Count(x => x.Origin == "scoped"), Is.EqualTo(1), "the global filter scoped the branch");
        Assert.That(rows.Count(x => x.Origin == "all"), Is.EqualTo(2), "IgnoringQueryFilters opted the branch out");
    }

    [Test]
    public async Task Union_rejects_unpushable_filters_and_misaligned_shapes()
    {
        using var db = await NewSchemaAsync();

        Assert.That(async () => await Uow(db).UnionAsync(UnionQuery.All(
                Union.Of<SaleOrder>().Where(x => x.Customer.Length > 4).Select(x => new { x.Customer }),
                Union.Of<SaleOrder>().Select(x => new { x.Customer }))),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("separate reads"),
            "a union cannot run part of a branch client-side");

        Assert.That(async () => await Uow(db).UnionAsync(UnionQuery.All(
                Union.Of<SaleOrder>().Select(x => new UnionRow { Name = x.Customer }),
                Union.Of<Buyer>().Select(x => new UnionRow { Origin = "buyer" }))),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("same shape"),
            "every branch must project the same members");
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

    /// <summary>A named grouped-projection target for the member-init GroupBy shape.</summary>
    private sealed class CustomerSummary
    {
        public string Customer { get; set; } = "";

        public int Orders { get; set; }

        public decimal Smallest { get; set; }
    }

    /// <summary>A named union-projection target for the member-init union shape.</summary>
    private sealed class UnionRow
    {
        public string Name { get; set; } = "";

        public string Origin { get; set; } = "";
    }
}
