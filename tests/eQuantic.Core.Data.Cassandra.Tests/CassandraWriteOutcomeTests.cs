using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Write;
using eQuantic.Core.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>A workspace's named value, keyed by workspace and name: the members that identify it are its primary key.</summary>
public sealed class SecretByName : IEntity<Guid>, IEntityTimeMark, IEntityTimeTrack
{
    public Guid WorkspaceId { get; set; }

    public string Name { get; set; } = "";

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Value { get; set; } = "";

    public int Uses { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid GetKey() => WorkspaceId;

    public void SetKey(Guid key) => WorkspaceId = key;
}

/// <summary>A label keyed by its name, which a deleted label can still hold.</summary>
public sealed class NamedLabel : IEntity<string>
{
    public string Name { get; set; } = "";

    public string Color { get; set; } = "";

    public DateTime? DeletedAt { get; set; }

    public string GetKey() => Name;

    public void SetKey(string key) => Name = key;
}

/// <summary>A queued job a worker claims with a compare-and-set.</summary>
public sealed class QueuedJob : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Status { get; set; } = "";

    public string? Owner { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A counter incremented and read in one call — a plain column, not a Cassandra counter.</summary>
public sealed class ItemType : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public int NextSequence { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A page two requests change member by member.</summary>
public sealed class Page : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = "";

    public bool Locked { get; set; }

    public string Body { get; set; } = "";

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A row whose number and stamp the store assigns.</summary>
public sealed class Voucher : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Note { get; set; } = "";

    public long Number { get; set; }

    public DateTimeOffset IssuedAt { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>
///     Proves, against a real Cassandra, upserts and inserts as lightweight transactions (<c>INSERT … IF NOT EXISTS</c>,
///     then <c>UPDATE … IF</c>), <c>DuplicateKeyException</c> from an insert that did not apply, set-based writes whose
///     conditions on members outside the key become the transaction's <c>IF</c>, returning updates and member-scoped
///     modifies.
/// </summary>
[TestFixture]
public sealed class CassandraWriteOutcomeTests : CassandraIntegrationTest
{
    private CassandraTestDatabase _db = null!;

    private static void Model(CassandraModelBuilder builder) => builder
        .Entity<SecretByName>(entity => entity.Table("secrets_by_name").PartitionKey(x => x.WorkspaceId).ClusteringKey(x => x.Name))
        .Entity<NamedLabel>(entity => entity.Table("named_labels").PartitionKey(x => x.Name))
        .Entity<QueuedJob>(entity => entity.Table("queued_jobs").PartitionKey(x => x.Id))
        .Entity<ItemType>(entity => entity.Table("item_types").PartitionKey(x => x.Id))
        .Entity<Page>(entity => entity.Table("pages").PartitionKey(x => x.Id))
        .Entity<Voucher>(entity => entity
            .Table("vouchers")
            .PartitionKey(x => x.Id)
            .Generated(x => x.Number)
            .Generated(x => x.IssuedAt, Modeling.StoreValue.Clock))
        .Entity<Tally>(entity => entity.Table("tallies").PartitionKey(x => x.Space).Counter(x => x.Hits));

    [SetUp]
    public async Task CreateKeyspace()
    {
        _db = new CassandraTestDatabase(CassandraTestServer.Host, CassandraTestServer.Port, "ks_" + Guid.NewGuid().ToString("N"),
            Model, [typeof(SchemaSetupMigration).Assembly]);
        await _db.Resolve<IMigrationExecutor>().ApplyAsync(new MigrationBuilder()
            .For<SecretByName>(table => table.EnsureCollection())
            .For<NamedLabel>(table => table.EnsureCollection())
            .For<QueuedJob>(table => table.EnsureCollection())
            .For<ItemType>(table => table.EnsureCollection())
            .For<Page>(table => table.EnsureCollection())
            .For<Voucher>(table => table.EnsureCollection())
            .For<Tally>(table => table.EnsureCollection())
            .Operations);
    }

    [TearDown]
    public void DropKeyspace() => _db?.Dispose();

    private IServiceScope NewScope() => _db.NewScope();

    private static T Resolve<T>(IServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    private static IAsyncRepository<TEntity, TKey> Repo<TEntity, TKey>(IServiceScope scope) where TEntity : class, IEntity<TKey> =>
        Resolve<IAsyncRepository<TEntity, TKey>>(scope);

    private static Task Commit(IServiceScope scope) => Resolve<IUnitOfWork>(scope).CommitAsync();

    [Test]
    public async Task An_upsert_inserts_then_updates()
    {
        using var scope = NewScope();
        var upserts = Resolve<IUpsertRepository<SecretByName, Guid>>(scope);
        var workspace = Guid.NewGuid();
        var secret = new SecretByName { WorkspaceId = workspace, Name = "token", Value = "a" };

        var inserted = await upserts.UpsertAsync(secret);
        Assert.That(inserted.Value, Is.EqualTo("a"));
        Assert.That(inserted.CreatedAt, Is.Not.EqualTo(default(DateTime)), "the insert stamps CreatedAt");
        Assert.That(inserted.UpdatedAt, Is.Null, "an insert is not an update");

        await Task.Delay(20);
        var updated = await upserts.UpsertAsync(new SecretByName { WorkspaceId = workspace, Name = "token", Value = "b" });
        Assert.That(updated.Value, Is.EqualTo("b"));
        Assert.That(updated.Id, Is.Not.EqualTo(secret.Id), "the update writes the entity's members (the key aside)");
        Assert.That(updated.CreatedAt, Is.EqualTo(inserted.CreatedAt).Within(TimeSpan.FromMilliseconds(1)), "the update keeps the insert's stamp");
        Assert.That(updated.UpdatedAt, Is.Not.Null, "the update stamps UpdatedAt");
        Assert.That((await Repo<SecretByName, Guid>(scope).GetFilteredAsync(x => x.WorkspaceId == workspace)).Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task An_upsert_on_the_compound_primary_key_applies_the_update_and_keeps_the_first_key()
    {
        var workspace = Guid.NewGuid();
        var first = new SecretByName { WorkspaceId = workspace, Name = "token", Value = "a" };
        using var scope = NewScope();
        var upserts = (IUpsertRepository<SecretByName, Guid>)Repo<SecretByName, Guid>(scope);
        await upserts.UpsertAsync(first, x => new { x.WorkspaceId, x.Name }, x => new SecretByName { Value = "b" });
        var stored = await upserts.UpsertAsync(new SecretByName { WorkspaceId = workspace, Name = "token", Value = "a" },
            x => new { x.WorkspaceId, x.Name }, x => new SecretByName { Value = "b" });

        var rows = (await Repo<SecretByName, Guid>(scope).GetFilteredAsync(x => x.WorkspaceId == workspace && x.Name == "token")).ToList();
        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Value, Is.EqualTo("b"));
        Assert.That(rows[0].Id, Is.EqualTo(first.Id), "the row keeps the identity the first call inserted");
        Assert.That(stored.Id, Is.EqualTo(first.Id));
        Assert.That(stored.Value, Is.EqualTo("b"));
    }

    /// <remarks>
    ///     Ten lightweight transactions on one partition contend in Paxos, and one can time out with its outcome unknown;
    ///     a serial read settles it, and an update of values set outright is written again when it did not apply. (A value
    ///     computed from the row is never written twice: its timeout reaches the caller — see the increment test.)
    /// </remarks>
    [Test]
    public async Task Ten_concurrent_upserts_of_one_target_leave_one_row_and_none_fails()
    {
        var workspace = Guid.NewGuid();
        var scopes = Enumerable.Range(0, 10).Select(_ => NewScope()).ToList();
        SecretByName[] results;
        try
        {
            results = await Task.WhenAll(scopes.Select((scope, index) => Task.Run(() =>
                Resolve<IUpsertRepository<SecretByName, Guid>>(scope).UpsertAsync(
                    new SecretByName { WorkspaceId = workspace, Name = "race", Value = "v" + index },
                    x => new { x.WorkspaceId, x.Name }, x => new SecretByName { Value = "v" + index }))));
        }
        finally
        {
            scopes.ForEach(scope => scope.Dispose());
        }

        using var check = NewScope();
        var rows = (await Repo<SecretByName, Guid>(check).GetFilteredAsync(x => x.WorkspaceId == workspace)).ToList();
        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(results.Select(result => result.Value), Does.Contain(rows[0].Value), "the row holds one call's write");
        Assert.That(results.Select(result => result.Id).Distinct().Count(), Is.EqualTo(1), "every call returns the one row");
    }

    [Test]
    public void An_upsert_on_members_outside_the_primary_key_is_refused()
    {
        using var scope = NewScope();
        var upserts = Resolve<IUpsertRepository<SecretByName, Guid>>(scope);

        var refusal = Assert.ThrowsAsync<NotSupportedException>(() =>
            upserts.UpsertAsync(new SecretByName { WorkspaceId = Guid.NewGuid(), Name = "token" }, x => new { x.Id }));
        Assert.That(refusal!.Message, Does.Contain("Cassandra holds no key unique but a table's primary key"));
        Assert.That(refusal.Message, Does.Contain("belong in the primary key"));
    }

    [Test]
    public async Task An_upsert_takes_store_generated_values_on_insert_and_never_writes_them_on_update()
    {
        using var scope = NewScope();
        var upserts = Resolve<IUpsertRepository<Voucher, Guid>>(scope);
        var voucher = new Voucher { Note = "first" };

        var inserted = await upserts.UpsertAsync(voucher);
        Assert.That(inserted.Number, Is.GreaterThan(0), "the insert took the sequence's number");
        Assert.That(inserted.IssuedAt, Is.Not.EqualTo(default(DateTimeOffset)), "the insert took the coordinator's clock");

        var updated = await upserts.UpsertAsync(new Voucher { Id = voucher.Id, Note = "second" });
        Assert.That(updated.Note, Is.EqualTo("second"));
        Assert.That(updated.Number, Is.EqualTo(inserted.Number), "the update left the sequence's number");
        Assert.That(updated.IssuedAt, Is.EqualTo(inserted.IssuedAt), "the update left the clock");

        var returned = await Resolve<IReturningUpdateRepository<Voucher, Guid>>(scope)
            .UpdateAndGetAsync(x => x.Id == voucher.Id, x => new Voucher { Note = "third" });
        Assert.That(returned.Single().Number, Is.EqualTo(inserted.Number), "a returning update hands back what the store assigned");
        Assert.That(returned.Single().Note, Is.EqualTo("third"));
    }

    [Test]
    public async Task A_conflict_filter_restricts_the_target_and_a_key_held_outside_it_is_taken()
    {
        using var scope = NewScope();
        var upserts = Resolve<IUpsertRepository<NamedLabel, string>>(scope);
        await upserts.UpsertAsync(new NamedLabel { Name = "urgent", Color = "red" });

        var updated = await upserts.UpsertAsync(new NamedLabel { Name = "urgent", Color = "blue" }, x => x.Name,
            conflictFilter: x => x.DeletedAt == null);
        Assert.That(updated.Color, Is.EqualTo("blue"), "a live row matches the filter: it is the target");

        await Repo<NamedLabel, string>(scope).UpdateManyAsync(x => x.Name == "urgent", x => new NamedLabel { DeletedAt = DateTime.UtcNow });
        Assert.ThrowsAsync<DuplicateKeyException>(() => upserts.UpsertAsync(new NamedLabel { Name = "urgent", Color = "green" }, x => x.Name,
            conflictFilter: x => x.DeletedAt == null), "a deleted row is not the target, and it holds the only key Cassandra has");
    }

    [Test]
    public async Task Inserting_many_skips_the_rows_already_there()
    {
        var workspace = Guid.NewGuid();
        var stored = new[]
        {
            new SecretByName { WorkspaceId = workspace, Name = "one", Value = "stored" },
            new SecretByName { WorkspaceId = workspace, Name = "two", Value = "stored" },
        };
        using (var seed = NewScope())
        {
            await Repo<SecretByName, Guid>(seed).AddRangeAsync(stored);
            await Commit(seed);
        }

        using var scope = NewScope();
        var inserted = await Resolve<IUpsertRepository<SecretByName, Guid>>(scope).InsertManyAsync(
        [
            new SecretByName { WorkspaceId = workspace, Name = "one", Value = "new" },
            new SecretByName { WorkspaceId = workspace, Name = "two", Value = "new" },
            new SecretByName { WorkspaceId = workspace, Name = "three", Value = "new" },
            new SecretByName { WorkspaceId = workspace, Name = "four", Value = "new" },
            new SecretByName { WorkspaceId = workspace, Name = "five", Value = "new" },
        ], skipDuplicates: true);

        Assert.That(inserted, Is.EqualTo(3L));
        var rows = (await Repo<SecretByName, Guid>(scope).GetFilteredAsync(x => x.WorkspaceId == workspace)).ToList();
        Assert.That(rows.Select(x => x.Name), Is.EquivalentTo(new[] { "one", "two", "three", "four", "five" }));
        Assert.That(rows.Where(x => x.Name == "one" || x.Name == "two").Select(x => x.Value), Is.All.EqualTo("stored"),
            "the two stored rows are unchanged");
    }

    [Test]
    public async Task Inserting_many_without_skipping_fails_on_a_stored_primary_key()
    {
        var workspace = Guid.NewGuid();
        using (var seed = NewScope())
        {
            await Repo<SecretByName, Guid>(seed).AddAsync(new SecretByName { WorkspaceId = workspace, Name = "taken", Value = "stored" });
            await Commit(seed);
        }

        using var scope = NewScope();
        Assert.ThrowsAsync<DuplicateKeyException>(() => Resolve<IUpsertRepository<SecretByName, Guid>>(scope).InsertManyAsync(
        [
            new SecretByName { WorkspaceId = workspace, Name = "fresh", Value = "new" },
            new SecretByName { WorkspaceId = workspace, Name = "taken", Value = "new" },
        ]));
        var rows = (await Repo<SecretByName, Guid>(scope).GetFilteredAsync(x => x.WorkspaceId == workspace)).ToList();
        Assert.That(rows.Single(x => x.Name == "taken").Value, Is.EqualTo("stored"), "the stored row was not overwritten");
    }

    [Test]
    public async Task Two_concurrent_compare_and_sets_match_once()
    {
        var job = new QueuedJob { Status = "queued" };
        using (var seed = NewScope())
        {
            await Repo<QueuedJob, Guid>(seed).AddAsync(job);
            await Commit(seed);
        }

        var workers = new[] { "worker-a", "worker-b" };
        var scopes = workers.Select(_ => NewScope()).ToList();
        long[] counts;
        try
        {
            counts = await Task.WhenAll(workers.Select((worker, index) => Task.Run(() =>
                Repo<QueuedJob, Guid>(scopes[index]).UpdateManyAsync(x => x.Id == job.Id && x.Status == "queued",
                    x => new QueuedJob { Status = "running", Owner = worker }))));
        }
        finally
        {
            scopes.ForEach(scope => scope.Dispose());
        }

        Assert.That(counts.OrderBy(count => count), Is.EqualTo(new[] { 0L, 1L }));
        using var check = NewScope();
        var claimed = (await Repo<QueuedJob, Guid>(check).GetAsync(job.Id))!;
        Assert.That(claimed.Status, Is.EqualTo("running"));
        Assert.That(claimed.Owner, Is.EqualTo(workers[Array.IndexOf(counts, 1L)]), "the row is the winner's");
    }

    [Test]
    public async Task A_set_based_write_counts_the_rows_it_matched()
    {
        var job = new QueuedJob { Status = "running" };
        using var scope = NewScope();
        await Repo<QueuedJob, Guid>(scope).AddAsync(job);
        await Commit(scope);

        var repo = Repo<QueuedJob, Guid>(scope);
        Assert.That(await repo.UpdateManyAsync(x => x.Id == job.Id, x => new QueuedJob { Status = "running" }), Is.EqualTo(1L));
        Assert.That(await repo.UpdateManyAsync(x => x.Id == job.Id && x.Status == "running", x => new QueuedJob { Status = "running" }),
            Is.EqualTo(1L), "a conditional update that writes the same value still matched");
        Assert.That(await repo.DeleteManyAsync(x => x.Id == job.Id && x.Status == "queued"), Is.EqualTo(0L));
        Assert.That(await repo.DeleteManyAsync(x => x.Id == job.Id && x.Status == "running"), Is.EqualTo(1L));
        Assert.That(await repo.GetAsync(job.Id), Is.Null);
    }

    [Test]
    public void A_condition_without_the_partition_key_is_refused()
    {
        using var scope = NewScope();
        var refusal = Assert.ThrowsAsync<NotSupportedException>(() =>
            Repo<QueuedJob, Guid>(scope).UpdateManyAsync(x => x.Status == "queued", x => new QueuedJob { Status = "running" }));
        Assert.That(refusal!.Message, Does.Contain("name the partition key"));
    }

    [Test]
    public void A_condition_on_a_counter_table_is_refused()
    {
        using var scope = NewScope();
        var refusal = Assert.ThrowsAsync<NotSupportedException>(() =>
            Repo<Tally, string>(scope).UpdateManyAsync(x => x.Space == "s" && x.Hits == 1, x => new Tally { Hits = x.Hits + 1 }));
        Assert.That(refusal!.Message, Does.Contain("counter table"));
    }

    [Test]
    public async Task Two_concurrent_increments_read_their_own_values()
    {
        var itemType = new ItemType { NextSequence = 7 };
        using (var seed = NewScope())
        {
            await Repo<ItemType, Guid>(seed).AddAsync(itemType);
            await Commit(seed);
        }

        var scopes = Enumerable.Range(0, 2).Select(_ => NewScope()).ToList();
        IReadOnlyList<ItemType>[] results;
        try
        {
            results = await Task.WhenAll(scopes.Select(scope => Task.Run(() =>
                Resolve<IReturningUpdateRepository<ItemType, Guid>>(scope).UpdateAndGetAsync(
                    x => x.Id == itemType.Id, x => new ItemType { NextSequence = x.NextSequence + 1 }))));
        }
        finally
        {
            scopes.ForEach(scope => scope.Dispose());
        }

        Assert.That(results.Select(rows => rows.Single().NextSequence).OrderBy(value => value), Is.EqualTo(new[] { 8, 9 }));
        using var check = NewScope();
        Assert.That((await Repo<ItemType, Guid>(check).GetAsync(itemType.Id))!.NextSequence, Is.EqualTo(9));
        Assert.That(await Resolve<IReturningUpdateRepository<ItemType, Guid>>(check)
            .UpdateAndGetAsync(x => x.Id == itemType.Id && x.NextSequence == 100, x => new ItemType { NextSequence = 0 }), Is.Empty);
    }

    /// <remarks>
    ///     Every row of one partition is written by a lightweight transaction of its own, and Paxos runs per partition: two
    ///     hundred proposed at once contend with each other until some time out, and an increment that timed out cannot be
    ///     written again. One write runs a partition's transactions one at a time, so none of them contends with another.
    /// </remarks>
    [Test]
    public async Task Two_hundred_rows_of_one_partition_take_a_conditional_update_an_increment_and_a_conditional_delete()
    {
        const int count = 200;
        var workspace = Guid.NewGuid();
        using (var seed = NewScope())
        {
            await Repo<SecretByName, Guid>(seed).AddRangeAsync(Enumerable.Range(0, count)
                .Select(index => new SecretByName { WorkspaceId = workspace, Name = $"name-{index:D3}", Value = "a" }));
            await Commit(seed);
        }

        using var scope = NewScope();
        var repo = Repo<SecretByName, Guid>(scope);
        Assert.That(await repo.UpdateManyAsync(x => x.WorkspaceId == workspace && x.Value == "a", x => new SecretByName { Value = "b" }),
            Is.EqualTo((long)count), "every row matched the condition, and each transaction applied");
        Assert.That(await repo.UpdateManyAsync(x => x.WorkspaceId == workspace, x => new SecretByName { Uses = x.Uses + 1 }),
            Is.EqualTo((long)count), "every increment applied, and none timed out");

        var rows = (await repo.GetFilteredAsync(x => x.WorkspaceId == workspace)).ToList();
        Assert.That(rows, Has.Count.EqualTo(count));
        Assert.That(rows.Select(x => x.Value), Is.All.EqualTo("b"));
        Assert.That(rows.Select(x => x.Uses), Is.All.EqualTo(1), "each row was incremented exactly once");

        Assert.That(await repo.DeleteManyAsync(x => x.WorkspaceId == workspace && x.Uses == 1), Is.EqualTo((long)count));
        Assert.That(await repo.GetFilteredAsync(x => x.WorkspaceId == workspace), Is.Empty);
    }

    [Test]
    public async Task Inserting_many_reserves_every_sequence_number_in_one_compare_and_set()
    {
        const int count = 100;
        using var executed = new ExecutedCql();
        using var scope = NewScope();
        var vouchers = Enumerable.Range(0, count).Select(index => new Voucher { Note = $"v{index}" }).ToList();

        var inserted = await Resolve<IUpsertRepository<Voucher, Guid>>(scope).InsertManyAsync(vouchers);

        Assert.That(inserted, Is.EqualTo((long)count));
        Assert.That(vouchers.Select(x => x.Number).Distinct().Count(), Is.EqualTo(count), "a hundred numbers, none handed out twice");
        Assert.That(vouchers.Select(x => x.Number).Order(), Is.EqualTo(Enumerable.Range(1, count).Select(number => (long)number)),
            "one reservation of a hundred numbers, in order");
        Assert.That(executed.On(CassandraSequences.TableName).Count(statement => statement.IsConditional), Is.EqualTo(1),
            "the hundred numbers are reserved by one compare-and-set");
        var stored = (await Repo<Voucher, Guid>(scope).GetAllAsync()).ToDictionary(x => x.Id);
        Assert.That(vouchers.All(voucher => stored[voucher.Id].Number == voucher.Number), Is.True, "each row holds the number its entity took");
    }

    /// <remarks>
    ///     The number a sequence hands out is on the entity before the insert, so only the coordinator's clock is read back,
    ///     and an <c>INSERT … IF NOT EXISTS</c> is read back at the serial consistency its transaction ran at: a read at the
    ///     session's (one replica) could miss the row the transaction just wrote.
    /// </remarks>
    [Test]
    public async Task A_compare_and_set_insert_reads_back_only_the_clock_at_the_serial_consistency()
    {
        using var executed = new ExecutedCql();
        using var scope = NewScope();
        var many = new[] { new Voucher { Note = "a" }, new Voucher { Note = "b" } };
        await Resolve<IUpsertRepository<Voucher, Guid>>(scope).InsertManyAsync(many);
        var upserted = await Resolve<IUpsertRepository<Voucher, Guid>>(scope).UpsertAsync(new Voucher { Note = "c" });

        Assert.That(many.Concat([upserted]).Select(x => x.IssuedAt), Has.None.EqualTo(default(DateTimeOffset)), "the clock was read back");
        var readBacks = executed.On("vouchers").Where(statement => statement.IsRead).ToList();
        Assert.That(readBacks, Has.Count.EqualTo(3), "one read back per inserted row");
        Assert.That(readBacks.Select(statement => statement.Cql), Has.All.Match(@"(?i)^SELECT\s+IssuedAt\s+FROM"),
            "the sequence's number is known: only the clock is read");
        Assert.That(readBacks.Select(statement => statement.Consistency), Has.All.EqualTo(nameof(global::Cassandra.ConsistencyLevel.Serial)),
            "the read back runs at the serial consistency of the transaction that wrote the row");
    }

    [Test]
    public async Task A_key_only_update_refuses_to_write_a_store_generated_member_or_the_key()
    {
        using var scope = NewScope();
        var voucher = await Resolve<IUpsertRepository<Voucher, Guid>>(scope).UpsertAsync(new Voucher { Note = "first" });
        var repo = Repo<Voucher, Guid>(scope);

        var number = Assert.ThrowsAsync<NotSupportedException>(() =>
            repo.UpdateManyAsync(x => x.Id == voucher.Id, x => new Voucher { Number = voucher.Number + 1000 }));
        Assert.That(number!.Message, Does.Contain("store-generated"));
        var stamp = Assert.ThrowsAsync<NotSupportedException>(() =>
            repo.UpdateManyAsync(x => x.Id == voucher.Id, x => new Voucher { IssuedAt = DateTimeOffset.UnixEpoch }));
        Assert.That(stamp!.Message, Does.Contain("store-generated"));
        var key = Assert.ThrowsAsync<NotSupportedException>(() =>
            repo.UpdateManyAsync(x => x.Id == voucher.Id, x => new Voucher { Id = Guid.NewGuid() }));
        Assert.That(key!.Message, Does.Contain("primary key"));

        var stored = (await repo.GetAsync(voucher.Id))!;
        Assert.That(stored.Number, Is.EqualTo(voucher.Number), "the sequence's number stands");
        Assert.That(stored.IssuedAt, Is.EqualTo(voucher.IssuedAt), "the clock stands");
    }

    [Test]
    public async Task A_member_scoped_modify_leaves_the_other_members()
    {
        var page = new Page { Title = "draft", Body = "text" };
        using (var seed = NewScope())
        {
            await Repo<Page, Guid>(seed).AddAsync(page);
            await Commit(seed);
        }

        using var first = NewScope();
        using var second = NewScope();
        var titled = (await Repo<Page, Guid>(first).GetAsync(page.Id))!;
        var locked = (await Repo<Page, Guid>(second).GetAsync(page.Id))!;
        titled.Title = "final";
        locked.Locked = true;
        await Resolve<IPartialModifyRepository<Page, Guid>>(first).ModifyAsync(titled, x => x.Title);
        ((IPartialModifyRepository<Page, Guid>)Repo<Page, Guid>(second)).Modify(locked, x => new { x.Locked });
        await Commit(first);
        await Commit(second);

        using var check = NewScope();
        var stored = (await Repo<Page, Guid>(check).GetAsync(page.Id))!;
        Assert.That(stored.Title, Is.EqualTo("final"));
        Assert.That(stored.Locked, Is.True);
        Assert.That(stored.Body, Is.EqualTo("text"));
    }
}
