using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
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

    // ---------------------------------------------------------------- guardrails (NotSupported)

    [Test]
    public async Task Non_key_filter_without_allow_filtering_is_rejected()
    {
        using var db = await NewSchemaAsync();
        var options = new QueryOptions<Account>().Where(x => x.Owner == "alice");

        Assert.That(async () => await AccountRepo(db).GetAllAsync(options), Throws.TypeOf<NotSupportedException>());
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
