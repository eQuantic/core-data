using eQuantic.Core.Data.Cassandra.Repository;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Read;
using global::Cassandra;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>
///     Exercises the native Cassandra repository against a real cluster: staged writes flushed on commit, point
///     lookups, key-scoped queries, <c>IN</c>, clustering ranges, <c>ORDER BY</c> a clustering key, <c>token()</c>
///     partition ranges, the <c>ALLOW FILTERING</c> opt-in, the guardrails that reject non-key writes/filters, and
///     an atomic <c>LOGGED BATCH</c> transaction.
/// </summary>
[TestFixture]
public sealed class CassandraRepositoryTests : CassandraIntegrationTest
{
    private static DateTime Utc(int hour) => new(2026, 1, 1, hour, 0, 0, DateTimeKind.Utc);

    private static Account NewAccount(string owner, decimal balance = 0m, bool active = true, List<string>? tags = null) =>
        new() { Id = Guid.NewGuid(), Owner = owner, Balance = balance, Active = active, Tags = tags ?? [], OpenedAt = Utc(0) };

    private static Reading NewReading(int sensor, DateTime at, double value = 1.0) =>
        new() { SensorId = sensor, At = at, Value = value, Quality = "ok" };

    // ---------------------------------------------------------------- writes + point lookups + materialization

    [Test]
    public async Task Add_commit_then_get_round_trips_every_column()
    {
        using var db = await NewSchemaAsync();
        var repo = AccountRepo(db);

        var account = new Account
        {
            Id = Guid.NewGuid(),
            Owner = "alice",
            Balance = 125.50m,
            Active = true,
            Tags = ["vip", "early"],
            OpenedAt = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
        };
        await repo.AddAsync(account);
        await Uow(db).CommitAsync();

        var loaded = await repo.GetAsync(account.Id);
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Owner, Is.EqualTo("alice"));
        Assert.That(loaded.Balance, Is.EqualTo(125.50m));
        Assert.That(loaded.Active, Is.True);
        Assert.That(loaded.Tags, Is.EquivalentTo(new[] { "vip", "early" }));
        Assert.That(loaded.OpenedAt, Is.EqualTo(account.OpenedAt).Within(TimeSpan.FromMilliseconds(1)));
    }

    [Test]
    public async Task Get_returns_null_for_a_missing_key()
    {
        using var db = await NewSchemaAsync();
        Assert.That(await AccountRepo(db).GetAsync(Guid.NewGuid()), Is.Null);
    }

    [Test]
    public async Task Staged_writes_are_invisible_until_commit()
    {
        using var db = await NewSchemaAsync();
        var repo = AccountRepo(db);
        var account = NewAccount("bob");

        await repo.AddAsync(account);
        Assert.That(await repo.GetAsync(account.Id), Is.Null, "staged, not yet flushed");

        await Uow(db).CommitAsync();
        Assert.That(await repo.GetAsync(account.Id), Is.Not.Null, "flushed on commit");
    }

    [Test]
    public async Task Remove_commit_deletes_the_row()
    {
        using var db = await NewSchemaAsync();
        var repo = AccountRepo(db);
        var account = NewAccount("carol");
        await Seed(db, account);

        await repo.RemoveAsync(account);
        await Uow(db).CommitAsync();

        Assert.That(await repo.GetAsync(account.Id), Is.Null);
    }

    // ---------------------------------------------------------------- key-scoped reads

    [Test]
    public async Task Get_filtered_by_partition_key_returns_the_matching_row()
    {
        using var db = await NewSchemaAsync();
        var repo = AccountRepo(db);
        var target = NewAccount("dave");
        await Seed(db, target, NewAccount("erin"), NewAccount("frank"));

        var found = (await repo.GetFilteredAsync(x => x.Id == target.Id)).ToList();

        Assert.That(found, Has.Count.EqualTo(1));
        Assert.That(found[0].Owner, Is.EqualTo("dave"));
    }

    [Test]
    public async Task Contains_over_the_partition_key_renders_an_IN_query()
    {
        using var db = await NewSchemaAsync();
        var repo = AccountRepo(db);
        var a = NewAccount("a");
        var b = NewAccount("b");
        var c = NewAccount("c");
        await Seed(db, a, b, c);

        var wanted = new[] { a.Id, c.Id };
        var found = await repo.GetFilteredAsync(x => wanted.Contains(x.Id));

        Assert.That(found.Select(x => x.Id), Is.EquivalentTo(wanted));
    }

    [Test]
    public async Task Count_and_any_reflect_committed_rows()
    {
        using var db = await NewSchemaAsync();
        var repo = AccountRepo(db);

        Assert.That(await repo.AnyAsync(), Is.False);
        await Seed(db, NewAccount("x"), NewAccount("y"), NewAccount("z"));

        Assert.That(await repo.CountAsync(), Is.EqualTo(3));
        Assert.That(await repo.AnyAsync(), Is.True);
    }

    // ---------------------------------------------------------------- immediate set-based writes (key-scoped)

    [Test]
    public async Task Delete_many_by_partition_key_removes_only_the_match()
    {
        using var db = await NewSchemaAsync();
        var repo = AccountRepo(db);
        var doomed = NewAccount("doomed");
        var kept = NewAccount("kept");
        await Seed(db, doomed, kept);

        var removed = await repo.DeleteManyAsync(x => x.Id == doomed.Id);

        Assert.That(removed, Is.EqualTo(1));
        Assert.That(await repo.GetAsync(doomed.Id), Is.Null);
        Assert.That(await repo.GetAsync(kept.Id), Is.Not.Null);
    }

    [Test]
    public async Task Update_many_by_partition_key_sets_the_declared_fields()
    {
        using var db = await NewSchemaAsync();
        var repo = AccountRepo(db);
        var account = NewAccount("mutable", balance: 10m, active: true);
        await Seed(db, account);

        var updated = await repo.UpdateManyAsync(x => x.Id == account.Id,
            _ => new Account { Active = false, Balance = 99.99m });

        Assert.That(updated, Is.EqualTo(1));
        var loaded = await repo.GetAsync(account.Id);
        Assert.That(loaded!.Active, Is.False);
        Assert.That(loaded.Balance, Is.EqualTo(99.99m));
        Assert.That(loaded.Owner, Is.EqualTo("mutable"), "columns not in the SET stay unchanged");
    }

    [Test]
    public async Task Update_many_appends_and_removes_collection_items_atomically()
    {
        using var db = await NewSchemaAsync();
        var repo = AccountRepo(db);
        var account = NewAccount("collector", tags: ["old", "keep"]);
        await Seed(db, account);

        await repo.UpdateManyAsync(x => x.Id == account.Id, x => new Account { Tags = x.Tags.Append("vip").ToList() });
        var gone = new[] { "old" };
        await repo.UpdateManyAsync(x => x.Id == account.Id, x => new Account { Tags = x.Tags.Except(gone).ToList() });

        var loaded = await repo.GetAsync(account.Id);
        Assert.That(loaded!.Tags, Is.EqualTo(new[] { "keep", "vip" }), "list + and - applied on the server");
    }

    [Test]
    public async Task Update_many_numeric_increment_is_rejected_with_counter_guidance()
    {
        using var db = await NewSchemaAsync();

        Assert.That(async () => await AccountRepo(db).UpdateManyAsync(x => x.Id == Guid.NewGuid(),
                x => new Account { Balance = x.Balance + 1m }),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("counter"));
    }

    // ---------------------------------------------------------------- counters + lightweight transactions

    [Test]
    public async Task Counter_columns_move_by_increments_and_read_back()
    {
        using var db = await NewSchemaAsync();
        var repo = db.Resolve<IAsyncRepository<Tally, string>>();

        await repo.UpdateManyAsync(x => x.Space == "api", x => new Tally { Hits = x.Hits + 5 });
        await repo.UpdateManyAsync(x => x.Space == "api", x => new Tally { Hits = x.Hits - 2 });

        var tally = await repo.GetAsync("api");
        Assert.That(tally, Is.Not.Null);
        Assert.That(tally!.Hits, Is.EqualTo(3), "counter incremented and decremented on the server");
    }

    [Test]
    public async Task Counter_tables_reject_inserts_with_guidance()
    {
        using var db = await NewSchemaAsync();
        var repo = db.Resolve<IAsyncRepository<Tally, string>>();

        Assert.That(async () => await repo.AddAsync(new Tally { Space = "api", Hits = 1 }),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("counter"));
    }

    [Test]
    public async Task Add_if_not_exists_applies_once_and_reports_the_conflict()
    {
        using var db = await NewSchemaAsync();
        var repo = (CassandraRepository<Account, Guid>)db.Resolve<IAsyncRepository<Account, Guid>>();
        var account = NewAccount("first", 10m);

        Assert.That(await repo.AddIfNotExistsAsync(account), Is.True, "the first insert applies");

        var conflicting = new Account { Id = account.Id, Owner = "second", Balance = 99m, Tags = [], OpenedAt = Utc(0) };
        Assert.That(await repo.AddIfNotExistsAsync(conflicting), Is.False, "the second insert reports the conflict");

        var loaded = await repo.GetAsync(account.Id);
        Assert.That(loaded!.Owner, Is.EqualTo("first"), "the original row is untouched");
    }

    // ---------------------------------------------------------------- ALLOW FILTERING opt-in

    [Test]
    public async Task Allow_filtering_opt_in_enables_a_non_key_scan()
    {
        using var db = await NewSchemaAsync();
        var repo = AccountRepo(db);
        await Seed(db, NewAccount("low", 50m), NewAccount("mid", 150m), NewAccount("high", 250m));

        var options = new QueryOptions<Account>().Where(x => x.Balance > 100m).AllowFiltering();
        var found = await repo.GetAllAsync(options);

        Assert.That(found.Select(x => x.Owner), Is.EquivalentTo(new[] { "mid", "high" }));
    }

    // ---------------------------------------------------------------- filter composition

    [Test]
    public async Task Get_filtered_composes_the_argument_filter_with_the_options_filter()
    {
        using var db = await NewSchemaAsync();
        var repo = AccountRepo(db);
        var low = NewAccount("low", 50m);
        var high = NewAccount("high", 250m);
        await Seed(db, low, high, NewAccount("other", 999m));

        var wanted = new[] { low.Id, high.Id };
        var options = new QueryOptions<Account>().Where(x => x.Balance > 100m).AllowFiltering();
        var balanceFilter = options.Filter;
        var found = (await repo.GetFilteredAsync(x => wanted.Contains(x.Id), options)).ToList();

        Assert.That(found.Select(x => x.Owner), Is.EquivalentTo(new[] { "high" }), "both filters apply (AND)");
        Assert.That(options.Filter, Is.SameAs(balanceFilter), "the caller's options are not mutated");
    }

    [Test]
    public async Task Get_honors_the_options_filter()
    {
        using var db = await NewSchemaAsync();
        var repo = AccountRepo(db);
        var dormant = NewAccount("dormant", active: false);
        await Seed(db, dormant);

        var onlyActive = new QueryOptions<Account>().Where(x => x.Active).AllowFiltering();

        Assert.That(await repo.GetAsync(dormant.Id, onlyActive), Is.Null, "the options filter narrows the point lookup");
        Assert.That(await repo.GetAsync(dormant.Id), Is.Not.Null, "without options the row is found");
    }

    // ---------------------------------------------------------------- pushdown + residual engine

    [Test]
    public async Task Not_equal_runs_client_side_within_the_pinned_partition()
    {
        using var db = await NewSchemaAsync();
        var repo = AccountRepo(db);
        var target = NewAccount("keep");
        await Seed(db, target, NewAccount("other"));

        var options = new QueryOptions<Account>().AllowClientEvaluation();
        var found = (await repo.GetFilteredAsync(x => x.Id == target.Id && x.Owner != "nope", options)).ToList();

        Assert.That(found, Has.Count.EqualTo(1));
        Assert.That(found[0].Owner, Is.EqualTo("keep"));
    }

    [Test]
    public async Task Residual_without_the_opt_in_is_rejected_with_guidance()
    {
        using var db = await NewSchemaAsync();
        var repo = AccountRepo(db);

        Assert.That(async () => await repo.GetFilteredAsync(x => x.Id == Guid.NewGuid() && x.Owner != "nope"),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("AllowClientEvaluation"));
    }

    [Test]
    public async Task Or_across_columns_runs_client_side_with_both_opt_ins()
    {
        using var db = await NewSchemaAsync();
        var repo = AccountRepo(db);
        await Seed(db, NewAccount("alice", 50m), NewAccount("bob", 250m), NewAccount("carol", 10m));

        var options = new QueryOptions<Account>().AllowClientEvaluation().AllowFiltering();
        var found = await repo.GetFilteredAsync(x => x.Owner == "alice" || x.Balance > 100m, options);

        Assert.That(found.Select(x => x.Owner), Is.EquivalentTo(new[] { "alice", "bob" }));
    }

    [Test]
    public async Task Unscoped_residual_also_requires_allow_filtering()
    {
        using var db = await NewSchemaAsync();
        var repo = AccountRepo(db);
        var options = new QueryOptions<Account>().AllowClientEvaluation();

        Assert.That(async () => await repo.GetFilteredAsync(x => x.Owner == "a" || x.Balance > 1m, options),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("AllowFiltering"));
    }

    [Test]
    public async Task Or_across_partitions_splits_into_parallel_native_queries()
    {
        using var db = await NewSchemaAsync();
        var repo = ReadingRepo(db);
        var cutoff = Utc(2);
        await Seed(db,
            NewReading(1, Utc(0)), NewReading(1, Utc(1)),
            NewReading(2, Utc(1)), NewReading(2, Utc(3)),
            NewReading(3, Utc(0)));

        // No opt-ins: every branch is a native partition query, so the split is the cheap path.
        var found = await repo.GetFilteredAsync(x => x.SensorId == 1 || (x.SensorId == 2 && x.At >= cutoff));

        Assert.That(found.Select(x => (x.SensorId, x.At)), Is.EquivalentTo(new[]
        {
            (1, Utc(0)), (1, Utc(1)), (2, Utc(3)),
        }));
    }

    [Test]
    public async Task Or_split_deduplicates_overlapping_branches_and_counts_correctly()
    {
        using var db = await NewSchemaAsync();
        var repo = ReadingRepo(db);
        var cutoff = Utc(1);
        await Seed(db, NewReading(1, Utc(0)), NewReading(1, Utc(2)));

        // Both branches match the Utc(2) row; the merge must de-duplicate it.
        var options = new QueryOptions<Reading>().Where(x => x.SensorId == 1 || (x.SensorId == 1 && x.At >= cutoff));
        var found = (await repo.GetAllAsync(options)).ToList();

        Assert.That(found, Has.Count.EqualTo(2));
        Assert.That(await repo.CountAsync(options), Is.EqualTo(2));
    }

    [Test]
    public async Task Count_applies_the_residual_filter()
    {
        using var db = await NewSchemaAsync();
        var repo = AccountRepo(db);
        var a = NewAccount("a");
        var b = NewAccount("b");
        await Seed(db, a, b);

        var wanted = new[] { a.Id, b.Id };
        var options = new QueryOptions<Account>().Where(x => wanted.Contains(x.Id) && x.Owner != "b").AllowClientEvaluation();

        Assert.That(await repo.CountAsync(options), Is.EqualTo(1));
    }

    // ---------------------------------------------------------------- server-side aggregates + projection

    [Test]
    public async Task Sum_of_a_member_selector_computes_on_the_cluster()
    {
        using var db = await NewSchemaAsync();
        var repo = AccountRepo(db);
        var a = NewAccount("a", 10.5m);
        var b = NewAccount("b", 20.25m);
        await Seed(db, a, b, NewAccount("c", 999m));

        var wanted = new[] { a.Id, b.Id };
        var sum = await repo.SumAsync(x => x.Balance, new QueryOptions<Account>().Where(x => wanted.Contains(x.Id)));

        Assert.That(sum, Is.EqualTo(30.75m));
    }

    [Test]
    public async Task Sum_of_a_computed_selector_still_works_client_side()
    {
        using var db = await NewSchemaAsync();
        var repo = AccountRepo(db);
        var a = NewAccount("a", 10m);
        await Seed(db, a);

        var sum = await repo.SumAsync(x => x.Balance * 2, new QueryOptions<Account>().Where(x => x.Id == a.Id));

        Assert.That(sum, Is.EqualTo(20m));
    }

    [Test]
    public async Task Get_mapped_projects_the_selected_columns()
    {
        using var db = await NewSchemaAsync();
        var repo = AccountRepo(db);
        var account = NewAccount("projected", 42m);
        await Seed(db, account);

        var owners = await repo.GetMappedAsync(x => new { x.Owner, x.Balance },
            new QueryOptions<Account>().Where(x => x.Id == account.Id));

        var projected = owners.Single();
        Assert.That(projected.Owner, Is.EqualTo("projected"));
        Assert.That(projected.Balance, Is.EqualTo(42m));
    }

    // ---------------------------------------------------------------- continuation paging

    [Test]
    public async Task Get_page_walks_the_native_paging_state_to_exhaustion()
    {
        using var db = await NewSchemaAsync();
        var repo = ReadingRepo(db);
        await Seed(db, NewReading(1, Utc(0)), NewReading(1, Utc(1)), NewReading(1, Utc(2)), NewReading(1, Utc(3)), NewReading(1, Utc(4)));

        var pager = (IContinuationReadRepository<Reading>)repo;
        var options = new QueryOptions<Reading>().Where(x => x.SensorId == 1);
        var seen = new List<DateTime>();
        string? token = null;
        var pages = 0;

        do
        {
            var page = await pager.GetPageAsync(2, token, options);
            Assert.That(page.Items, Has.Count.LessThanOrEqualTo(2));
            seen.AddRange(page.Items.Select(x => x.At));
            token = page.ContinuationToken;
            pages++;
        } while (token is not null && pages < 10);

        Assert.That(seen, Has.Count.EqualTo(5));
        Assert.That(seen, Is.Unique, "no row repeats across pages");
        Assert.That(pages, Is.GreaterThanOrEqualTo(3), "the read spans multiple pages");
    }

    // ---------------------------------------------------------------- explain

    [Test]
    public async Task Explain_reports_the_pushed_statement_and_the_gates()
    {
        using var db = await NewSchemaAsync();
        var explainable = (IExplainableRepository<Account>)AccountRepo(db);

        var plan = explainable.Explain(new QueryOptions<Account>().Where(x => x.Owner != "x"));

        Assert.That(plan.Provider, Is.EqualTo("Cassandra"));
        Assert.That(plan.Statement, Does.StartWith("SELECT * FROM"));
        Assert.That(plan.ClientEvaluation, Is.True);
        Assert.That(plan.Residual, Does.Contain("Owner"));
        Assert.That(plan.Notes, Has.Some.Contains("AllowClientEvaluation"));
    }

    [Test]
    public async Task Explain_of_a_key_scoped_filter_is_fully_pushed_down()
    {
        using var db = await NewSchemaAsync();
        var explainable = (IExplainableRepository<Account>)AccountRepo(db);
        var id = Guid.NewGuid();

        var plan = explainable.Explain(new QueryOptions<Account>().Where(x => x.Id == id));

        Assert.That(plan.Statement, Does.Contain("WHERE Id = ?"));
        Assert.That(plan.Parameters, Is.EqualTo(new object?[] { id }));
        Assert.That(plan.ClientEvaluation, Is.False);
        Assert.That(plan.PartitionScoped, Is.True);
    }

    // ---------------------------------------------------------------- guardrails (NotSupported)

    [Test]
    public async Task Non_key_filter_without_allow_filtering_is_rejected()
    {
        using var db = await NewSchemaAsync();
        var options = new QueryOptions<Account>().Where(x => x.Owner == "alice");

        Assert.That(async () => await AccountRepo(db).GetAllAsync(options), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public async Task Include_is_rejected_with_a_clear_message()
    {
        using var db = await NewSchemaAsync();
        var options = new QueryOptions<Account>().Include(nameof(Account.Tags));

        Assert.That(async () => await AccountRepo(db).GetAllAsync(options),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("self-contained"));
    }

    [Test]
    public async Task Or_across_columns_is_rejected()
    {
        using var db = await NewSchemaAsync();
        var options = new QueryOptions<Account>().Where(x => x.Id == Guid.NewGuid() || x.Balance > 1m);

        Assert.That(async () => await AccountRepo(db).GetAllAsync(options), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public async Task Delete_many_on_a_non_key_column_is_rejected()
    {
        using var db = await NewSchemaAsync();
        Assert.That(async () => await AccountRepo(db).DeleteManyAsync(x => x.Owner == "alice"),
            Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public async Task Update_many_on_a_non_key_column_is_rejected()
    {
        using var db = await NewSchemaAsync();
        Assert.That(async () => await AccountRepo(db).UpdateManyAsync(x => x.Owner == "alice",
            _ => new Account { Active = false }), Throws.TypeOf<NotSupportedException>());
    }

    // ---------------------------------------------------------------- clustering ranges + ORDER BY + token()

    [Test]
    public async Task Partition_equality_and_clustering_range_query_natively()
    {
        using var db = await NewSchemaAsync();
        var repo = ReadingRepo(db);
        await Seed(db, NewReading(7, Utc(0)), NewReading(7, Utc(1)), NewReading(7, Utc(2)));

        var options = new QueryOptions<Reading>().Where(x => x.SensorId == 7 && x.At >= Utc(1));
        var found = await repo.GetAllAsync(options);

        Assert.That(found.Select(r => r.At), Is.EquivalentTo(new[] { Utc(1), Utc(2) }));
    }

    [Test]
    public async Task Order_by_a_clustering_key_descending()
    {
        using var db = await NewSchemaAsync();
        var repo = ReadingRepo(db);
        await Seed(db, NewReading(7, Utc(0)), NewReading(7, Utc(2)), NewReading(7, Utc(1)));

        var options = new QueryOptions<Reading>().Where(x => x.SensorId == 7).OrderByDescending(x => x.At);
        var found = (await repo.GetAllAsync(options)).ToList();

        Assert.That(found.Select(r => r.At), Is.Ordered.Descending);
        Assert.That(found[0].At, Is.EqualTo(Utc(2)).Within(TimeSpan.FromMilliseconds(1)));
    }

    [Test]
    public async Task Order_by_a_non_clustering_key_is_rejected()
    {
        using var db = await NewSchemaAsync();
        await Seed(db, NewReading(7, Utc(0)));
        var options = new QueryOptions<Reading>().Where(x => x.SensorId == 7).OrderBy(x => x.Value);

        Assert.That(async () => await ReadingRepo(db).GetAllAsync(options), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public async Task Range_on_the_partition_key_uses_token_and_matches_a_raw_token_query()
    {
        using var db = await NewSchemaAsync();
        var repo = ReadingRepo(db);
        await Seed(db, Enumerable.Range(1, 20).Select(sensor => NewReading(sensor, Utc(0))).ToArray());

        // token() orders by the partition hash, not the value — so derive ground truth from the cluster itself:
        // pick the partition with the smallest token as the pivot, then everything strictly above it is non-empty.
        var tokens = (await db.Session.ExecuteAsync(new SimpleStatement("SELECT sensorid, token(sensorid) AS tk FROM readings")))
            .Select(row => (Sensor: row.GetValue<int>("sensorid"), Token: row.GetValue<long>("tk")))
            .ToList();
        var pivot = tokens.OrderBy(t => t.Token).First();
        var expected = tokens.Where(t => t.Token > pivot.Token).Select(t => t.Sensor).OrderBy(s => s).ToList();

        var viaProvider = (await repo.GetFilteredAsync(x => x.SensorId > pivot.Sensor))
            .Select(r => r.SensorId).OrderBy(s => s).ToList();

        Assert.That(viaProvider, Is.Not.Empty);
        Assert.That(viaProvider, Is.EqualTo(expected), "the provider's token() range must match a hand-written token() query");
    }

    // ---------------------------------------------------------------- explicit transaction (LOGGED BATCH)

    [Test]
    public async Task Transaction_commits_its_writes_atomically_as_a_logged_batch()
    {
        using var db = await NewSchemaAsync();
        var uow = Uow(db);
        var repo = AccountRepo(db);
        var a = NewAccount("batch-a");
        var b = NewAccount("batch-b");

        await uow.BeginTransactionAsync();
        await repo.AddAsync(a);
        await repo.AddAsync(b);
        Assert.That(await repo.CountAsync(), Is.EqualTo(0), "deferred until the batch runs");

        await uow.CommitTransactionAsync();
        Assert.That(await repo.CountAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task Rollback_transaction_discards_the_deferred_writes()
    {
        using var db = await NewSchemaAsync();
        var uow = Uow(db);
        var repo = AccountRepo(db);

        await uow.BeginTransactionAsync();
        await repo.AddAsync(NewAccount("ghost"));
        await uow.RollbackTransactionAsync();

        Assert.That(await repo.CountAsync(), Is.EqualTo(0));
    }
}
