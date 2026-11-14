using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Write;
using eQuantic.Core.Domain.Entities;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.CosmosDb.Tests;

/// <summary>A workspace's named value, unique by workspace and name within its partition (the container's unique key).</summary>
public sealed class Secret : IEntity<string>, IEntityTimeMark, IEntityTimeTrack
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Category { get; set; } = "";

    public string WorkspaceId { get; set; } = "";

    public string Name { get; set; } = "";

    public string Value { get; set; } = "";

    public int Uses { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>A label whose live name is the upsert target (Cosmos DB has no partial unique key).</summary>
public sealed class Label : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Category { get; set; } = "";

    public string Name { get; set; } = "";

    public string Color { get; set; } = "";

    public DateTime? DeletedAt { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>A queued job a worker claims with a compare-and-set.</summary>
public sealed class Job : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Category { get; set; } = "";

    public string Status { get; set; } = "";

    public string? Owner { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>A counter incremented and read in one call.</summary>
public sealed class ItemType : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Category { get; set; } = "";

    public int NextSequence { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>A page two requests change member by member.</summary>
public sealed class Page : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Category { get; set; } = "";

    public string Title { get; set; } = "";

    public bool Locked { get; set; }

    public string Body { get; set; } = "";

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>
///     A record with more members than Cosmos DB patches in one operation: an update of all twelve, with the status, goes
///     as several patches of the item in one transactional batch. Its <c>_etag</c> is its concurrency token.
/// </summary>
public sealed class WideRecord : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Category { get; set; } = "";

    public string Status { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("_etag")]
    public string? ETag { get; set; }

    public string F01 { get; set; } = "";
    public string F02 { get; set; } = "";
    public string F03 { get; set; } = "";
    public string F04 { get; set; } = "";
    public string F05 { get; set; } = "";
    public string F06 { get; set; } = "";
    public string F07 { get; set; } = "";
    public string F08 { get; set; } = "";
    public string F09 { get; set; } = "";
    public string F10 { get; set; } = "";
    public string F11 { get; set; } = "";
    public int F12 { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;

    /// <summary>The twelve members, in order, to compare at once.</summary>
    public object[] Members() => [F01, F02, F03, F04, F05, F06, F07, F08, F09, F10, F11, F12];

    /// <summary>Sets the twelve members to values derived from one tag.</summary>
    public WideRecord Fill(string tag, int number)
    {
        F01 = tag + "1"; F02 = tag + "2"; F03 = tag + "3"; F04 = tag + "4"; F05 = tag + "5"; F06 = tag + "6";
        F07 = tag + "7"; F08 = tag + "8"; F09 = tag + "9"; F10 = tag + "10"; F11 = tag + "11"; F12 = number;
        return this;
    }
}

/// <summary>An account whose e-mail is the container's unique key.</summary>
public sealed class UserAccount : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Category { get; set; } = "";

    public string Email { get; set; } = "";

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>
///     Proves, against the Cosmos DB emulator, upserts (a create or a replace guarded by the <c>_etag</c>), inserts
///     skipping duplicates (creates discarding only 409), <c>DuplicateKeyException</c>, set-based writes conditioned on
///     their filter (a patch under <c>FilterPredicate</c>, a delete under the <c>_etag</c> of a read that still matched),
///     returning updates and member-scoped modifies (a patch of the members named).
/// </summary>
[TestFixture]
public sealed class CosmosWriteOutcomeTests : CosmosIntegrationTest
{
    private static IServiceScope NewScope() => CosmosTestServer.Provider.CreateScope();

    private static T Resolve<T>(IServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    private static IAsyncRepository<TEntity, string> RepoOf<TEntity>(IServiceScope scope) where TEntity : class, IEntity<string> =>
        Resolve<IAsyncRepository<TEntity, string>>(scope);

    private static Task Commit(IServiceScope scope) => Resolve<IUnitOfWork>(scope).CommitAsync();

    [Test]
    public async Task An_upsert_inserts_then_updates()
    {
        using var scope = NewScope();
        var upserts = Resolve<IUpsertRepository<Secret, string>>(scope);
        var secret = new Secret { Category = Partition, WorkspaceId = "w", Name = "token", Value = "a" };

        var inserted = await upserts.UpsertAsync(secret);
        Assert.That(inserted.Value, Is.EqualTo("a"));
        Assert.That(inserted.CreatedAt, Is.Not.EqualTo(default(DateTime)), "the insert stamps CreatedAt");
        Assert.That(inserted.UpdatedAt, Is.Null, "an insert is not an update");

        await Task.Delay(20);
        var updated = await upserts.UpsertAsync(new Secret { Id = secret.Id, Category = Partition, WorkspaceId = "w", Name = "token", Value = "b" });
        Assert.That(updated.Value, Is.EqualTo("b"));
        Assert.That(updated.CreatedAt, Is.EqualTo(inserted.CreatedAt).Within(TimeSpan.FromMilliseconds(1)), "the update keeps the insert's stamp");
        Assert.That(updated.UpdatedAt, Is.Not.Null, "the update stamps UpdatedAt");
        Assert.That((await RepoOf<Secret>(scope).GetFilteredAsync(x => x.Category == Partition)).Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task An_upsert_on_a_compound_unique_key_applies_the_update_and_keeps_the_first_key()
    {
        var first = new Secret { Category = Partition, WorkspaceId = "w", Name = "token", Value = "a" };
        using var scope = NewScope();
        var upserts = (IUpsertRepository<Secret, string>)RepoOf<Secret>(scope);
        await upserts.UpsertAsync(first, x => new { x.WorkspaceId, x.Name }, x => new Secret { Value = "b" });
        var stored = await upserts.UpsertAsync(new Secret { Category = Partition, WorkspaceId = "w", Name = "token", Value = "a" },
            x => new { x.WorkspaceId, x.Name }, x => new Secret { Value = "b" });

        var rows = (await RepoOf<Secret>(scope).GetFilteredAsync(x => x.Category == Partition)).ToList();
        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Value, Is.EqualTo("b"));
        Assert.That(rows[0].Id, Is.EqualTo(first.Id), "the item keeps the key the first call inserted");
        Assert.That(stored.Id, Is.EqualTo(first.Id));
        Assert.That(stored.Value, Is.EqualTo("b"));
    }

    [Test]
    public async Task Ten_concurrent_upserts_of_one_target_leave_one_row_and_none_fails()
    {
        var partition = Partition;
        var scopes = Enumerable.Range(0, 10).Select(_ => NewScope()).ToList();
        try
        {
            await Task.WhenAll(scopes.Select(scope => Task.Run(() =>
                Resolve<IUpsertRepository<Secret, string>>(scope).UpsertAsync(
                    new Secret { Category = partition, WorkspaceId = "race", Name = "race", Value = "v" },
                    x => new { x.WorkspaceId, x.Name }, x => new Secret { Uses = x.Uses + 1 }))));
        }
        finally
        {
            scopes.ForEach(scope => scope.Dispose());
        }

        using var check = NewScope();
        var rows = (await RepoOf<Secret>(check).GetFilteredAsync(x => x.Category == partition)).ToList();
        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Uses, Is.EqualTo(9), "one call inserted (Uses = 0) and the nine others each added one");
    }

    [Test]
    public async Task Ten_concurrent_upserts_of_one_id_leave_one_item_and_none_fails()
    {
        var partition = Partition;
        var id = Guid.NewGuid().ToString("N");
        var scopes = Enumerable.Range(0, 10).Select(_ => NewScope()).ToList();
        try
        {
            await Task.WhenAll(scopes.Select((scope, index) => Task.Run(() =>
                Resolve<IUpsertRepository<Job, string>>(scope).UpsertAsync(
                    new Job { Id = id, Category = partition, Status = "queued", Owner = "w" + index }))));
        }
        finally
        {
            scopes.ForEach(scope => scope.Dispose());
        }

        using var check = NewScope();
        Assert.That((await RepoOf<Job>(check).GetFilteredAsync(x => x.Category == partition)).Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task An_upsert_takes_store_generated_values_on_insert_and_never_writes_them_on_update()
    {
        using var scope = NewScope();
        var upserts = Resolve<IUpsertRepository<Receipt, string>>(scope);
        var receipt = new Receipt { Category = Partition, Note = "first" };

        var inserted = await upserts.UpsertAsync(receipt);
        Assert.That(inserted.Number, Is.GreaterThan(0), "the insert took the counter's number");
        Assert.That(inserted.IssuedAt, Is.Not.EqualTo(default(DateTimeOffset)), "the insert took the server's clock");

        var updated = await upserts.UpsertAsync(new Receipt { Id = receipt.Id, Category = Partition, Note = "second" });
        Assert.That(updated.Note, Is.EqualTo("second"));
        Assert.That(updated.Number, Is.EqualTo(inserted.Number), "the update left the counter's number");
        Assert.That(updated.IssuedAt, Is.EqualTo(inserted.IssuedAt), "the update left the clock");

        var returned = await Resolve<IReturningUpdateRepository<Receipt, string>>(scope)
            .UpdateAndGetAsync(x => x.Id == receipt.Id, x => new Receipt { Note = "third" });
        Assert.That(returned.Single().Number, Is.EqualTo(inserted.Number), "a returning update hands back what the store assigned");
        Assert.That(returned.Single().Note, Is.EqualTo("third"));
    }

    [Test]
    public async Task A_conflict_filter_restricts_the_target()
    {
        using var scope = NewScope();
        var repo = RepoOf<Label>(scope);
        var deleted = new Label { Category = Partition, Name = "urgent", Color = "grey", DeletedAt = DateTime.UtcNow };
        await repo.AddAsync(deleted);
        await Commit(scope);

        var upserts = (IUpsertRepository<Label, string>)repo;
        var live = await upserts.UpsertAsync(new Label { Category = Partition, Name = "urgent", Color = "red" }, x => x.Name,
            conflictFilter: x => x.DeletedAt == null);
        Assert.That(live.Id, Is.Not.EqualTo(deleted.Id), "the deleted item was not the target");
        var again = await upserts.UpsertAsync(new Label { Category = Partition, Name = "urgent", Color = "blue" }, x => x.Name,
            conflictFilter: x => x.DeletedAt == null);
        Assert.That(again.Id, Is.EqualTo(live.Id));
        Assert.That(again.Color, Is.EqualTo("blue"));

        var rows = (await repo.GetFilteredAsync(x => x.Category == Partition)).ToList();
        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows.Single(x => x.Id == deleted.Id).Color, Is.EqualTo("grey"));
    }

    [Test]
    public async Task Inserting_many_skips_the_rows_already_there()
    {
        var stored = new[]
        {
            new Secret { Category = Partition, WorkspaceId = "w", Name = "one", Value = "stored" },
            new Secret { Category = Partition, WorkspaceId = "w", Name = "two", Value = "stored" },
        };
        using (var seed = NewScope())
        {
            await RepoOf<Secret>(seed).AddRangeAsync(stored);
            await Commit(seed);
        }

        using var scope = NewScope();
        var inserted = await Resolve<IUpsertRepository<Secret, string>>(scope).InsertManyAsync(
        [
            new Secret { Id = stored[0].Id, Category = Partition, WorkspaceId = "w", Name = "one-again", Value = "new" },
            new Secret { Category = Partition, WorkspaceId = "w", Name = "two", Value = "new" },
            new Secret { Category = Partition, WorkspaceId = "w", Name = "three", Value = "new" },
            new Secret { Category = Partition, WorkspaceId = "w", Name = "four", Value = "new" },
            new Secret { Category = Partition, WorkspaceId = "w", Name = "five", Value = "new" },
        ], skipDuplicates: true);

        Assert.That(inserted, Is.EqualTo(3L));
        var rows = (await RepoOf<Secret>(scope).GetFilteredAsync(x => x.Category == Partition)).ToList();
        Assert.That(rows.Select(x => x.Name), Is.EquivalentTo(new[] { "one", "two", "three", "four", "five" }));
        Assert.That(rows.Where(x => x.Name == "one" || x.Name == "two").Select(x => x.Value), Is.All.EqualTo("stored"));
    }

    [Test]
    public async Task Inserting_many_without_skipping_fails_on_a_taken_key()
    {
        var stored = new Secret { Category = Partition, WorkspaceId = "w", Name = "taken", Value = "stored" };
        using (var seed = NewScope())
        {
            await RepoOf<Secret>(seed).AddAsync(stored);
            await Commit(seed);
        }

        using var scope = NewScope();
        var exception = Assert.ThrowsAsync<DuplicateKeyException>(() => Resolve<IUpsertRepository<Secret, string>>(scope).InsertManyAsync(
        [
            new Secret { Category = Partition, WorkspaceId = "w", Name = "fresh", Value = "new" },
            new Secret { Category = Partition, WorkspaceId = "w", Name = "taken", Value = "new" },
        ]));
        Assert.That(exception!.InnerException, Is.InstanceOf<CosmosException>());
        Assert.That(exception.ConstraintName, Is.Null, "Cosmos DB names no key");
    }

    [Test]
    public async Task A_staged_insert_of_a_taken_key_fails_the_commit()
    {
        var stored = new Job { Category = Partition, Status = "queued" };
        using (var seed = NewScope())
        {
            await RepoOf<Job>(seed).AddAsync(stored);
            await Commit(seed);
        }

        using var scope = NewScope();
        await RepoOf<Job>(scope).AddAsync(new Job { Id = stored.Id, Category = Partition, Status = "again" });
        var exception = Assert.ThrowsAsync<DuplicateKeyException>(() => Commit(scope));
        Assert.That(exception!.InnerException, Is.InstanceOf<CosmosException>(), "the SDK's exception is kept");
        Assert.That((await RepoOf<Job>(scope).GetAsync(stored.Id))!.Status, Is.EqualTo("queued"));
    }

    [Test]
    public async Task A_staged_insert_of_a_taken_key_in_a_transaction_writes_nothing()
    {
        var stored = new Job { Category = Partition, Status = "queued" };
        using (var seed = NewScope())
        {
            await RepoOf<Job>(seed).AddAsync(stored);
            await Commit(seed);
        }

        using var scope = NewScope();
        var unitOfWork = Resolve<Repository.CosmosDefaultUnitOfWork>(scope);
        var fresh = new Job { Category = Partition, Status = "fresh" };
        await unitOfWork.BeginTransactionAsync();
        await RepoOf<Job>(scope).AddAsync(fresh);
        await RepoOf<Job>(scope).AddAsync(new Job { Id = stored.Id, Category = Partition, Status = "again" });
        await unitOfWork.CommitAsync();
        Assert.ThrowsAsync<DuplicateKeyException>(() => unitOfWork.CommitTransactionAsync());

        using var check = NewScope();
        Assert.That(await RepoOf<Job>(check).GetAsync(fresh.Id), Is.Null, "the transactional batch is atomic");
    }

    [Test]
    public async Task A_unique_key_other_than_the_id_fails_with_DuplicateKeyException_on_every_write_path()
    {
        var owner = new UserAccount { Category = Partition, Email = "owner@example.test" };
        var other = new UserAccount { Category = Partition, Email = "other@example.test" };
        using (var seed = NewScope())
        {
            await RepoOf<UserAccount>(seed).AddRangeAsync([owner, other]);
            await Commit(seed);
        }

        using var scope = NewScope();
        var repo = RepoOf<UserAccount>(scope);
        await repo.AddAsync(new UserAccount { Category = Partition, Email = owner.Email });
        Assert.ThrowsAsync<DuplicateKeyException>(() => Commit(scope), "a staged insert");

        var upserts = (IUpsertRepository<UserAccount, string>)repo;
        Assert.ThrowsAsync<DuplicateKeyException>(() => upserts.InsertManyAsync([new UserAccount { Category = Partition, Email = owner.Email }]),
            "a batch insert");
        Assert.ThrowsAsync<DuplicateKeyException>(() =>
            repo.UpdateManyAsync(x => x.Id == other.Id, x => new UserAccount { Email = owner.Email }), "a set-based update");
        Assert.ThrowsAsync<DuplicateKeyException>(() =>
            upserts.UpsertAsync(new UserAccount { Category = Partition, Email = owner.Email }), "an upsert on the id that takes the unique key");
    }

    [Test]
    public async Task Two_concurrent_compare_and_sets_match_once()
    {
        var job = new Job { Category = Partition, Status = "queued" };
        using (var seed = NewScope())
        {
            await RepoOf<Job>(seed).AddAsync(job);
            await Commit(seed);
        }

        var workers = new[] { "worker-a", "worker-b" };
        var scopes = workers.Select(_ => NewScope()).ToList();
        long[] counts;
        try
        {
            counts = await Task.WhenAll(workers.Select((worker, index) => Task.Run(() =>
                RepoOf<Job>(scopes[index]).UpdateManyAsync(x => x.Id == job.Id && x.Status == "queued",
                    x => new Job { Status = "running", Owner = worker }))));
        }
        finally
        {
            scopes.ForEach(scope => scope.Dispose());
        }

        Assert.That(counts.OrderBy(count => count), Is.EqualTo(new[] { 0L, 1L }));
        using var check = NewScope();
        var claimed = (await RepoOf<Job>(check).GetAsync(job.Id))!;
        Assert.That(claimed.Status, Is.EqualTo("running"));
        Assert.That(claimed.Owner, Is.EqualTo(workers[Array.IndexOf(counts, 1L)]), "the item is the winner's");
    }

    [Test]
    public async Task A_set_based_write_counts_the_items_it_matched()
    {
        var job = new Job { Category = Partition, Status = "running" };
        using var scope = NewScope();
        await RepoOf<Job>(scope).AddAsync(job);
        await Commit(scope);

        Assert.That(await RepoOf<Job>(scope).UpdateManyAsync(x => x.Id == job.Id, x => new Job { Status = "running" }), Is.EqualTo(1L));
        Assert.That(await RepoOf<Job>(scope).DeleteManyAsync(x => x.Id == job.Id && x.Status == "queued"), Is.EqualTo(0L));
        Assert.That(await RepoOf<Job>(scope).DeleteManyAsync(x => x.Id == job.Id && x.Status == "running"), Is.EqualTo(1L));
    }

    [Test]
    public async Task Two_concurrent_increments_read_their_own_values()
    {
        var itemType = new ItemType { Category = Partition, NextSequence = 7 };
        using (var seed = NewScope())
        {
            await RepoOf<ItemType>(seed).AddAsync(itemType);
            await Commit(seed);
        }

        var scopes = Enumerable.Range(0, 2).Select(_ => NewScope()).ToList();
        IReadOnlyList<ItemType>[] results;
        try
        {
            results = await Task.WhenAll(scopes.Select(scope => Task.Run(() =>
                Resolve<IReturningUpdateRepository<ItemType, string>>(scope).UpdateAndGetAsync(
                    x => x.Id == itemType.Id, x => new ItemType { NextSequence = x.NextSequence + 1 }))));
        }
        finally
        {
            scopes.ForEach(scope => scope.Dispose());
        }

        Assert.That(results.Select(rows => rows.Single().NextSequence).OrderBy(value => value), Is.EqualTo(new[] { 8, 9 }));
        using var check = NewScope();
        Assert.That((await RepoOf<ItemType>(check).GetAsync(itemType.Id))!.NextSequence, Is.EqualTo(9));
        Assert.That(await Resolve<IReturningUpdateRepository<ItemType, string>>(check)
            .UpdateAndGetAsync(x => x.Id == itemType.Id && x.NextSequence == 100, x => new ItemType { NextSequence = 0 }), Is.Empty);
    }

    [Test]
    public async Task A_member_scoped_modify_leaves_the_other_members()
    {
        var page = new Page { Category = Partition, Title = "draft", Body = "text" };
        using (var seed = NewScope())
        {
            await RepoOf<Page>(seed).AddAsync(page);
            await Commit(seed);
        }

        using var first = NewScope();
        using var second = NewScope();
        var titled = (await RepoOf<Page>(first).GetAsync(page.Id))!;
        var locked = (await RepoOf<Page>(second).GetAsync(page.Id))!;
        titled.Title = "final";
        locked.Locked = true;
        await Resolve<IPartialModifyRepository<Page, string>>(first).ModifyAsync(titled, x => x.Title);
        ((IPartialModifyRepository<Page, string>)RepoOf<Page>(second)).Modify(locked, x => new { x.Locked });
        await Commit(first);
        await Commit(second);

        using var check = NewScope();
        var stored = (await RepoOf<Page>(check).GetAsync(page.Id))!;
        Assert.That(stored.Title, Is.EqualTo("final"));
        Assert.That(stored.Locked, Is.True);
        Assert.That(stored.Body, Is.EqualTo("text"));
    }
    // ---------------------------------------------------------------- more paths than one patch takes

    private static readonly System.Linq.Expressions.Expression<Func<WideRecord, object?>> TwelveMembers = x => new
    {
        x.F01, x.F02, x.F03, x.F04, x.F05, x.F06, x.F07, x.F08, x.F09, x.F10, x.F11, x.F12,
    };

    private async Task<WideRecord> SeedWideAsync(string status = "queued")
    {
        var record = new WideRecord { Category = Partition, Status = status }.Fill("seed", 0);
        using var seed = NewScope();
        await RepoOf<WideRecord>(seed).AddAsync(record);
        await Commit(seed);
        return record;
    }

    private static async Task<WideRecord> ReadWideAsync(string id)
    {
        using var check = NewScope();
        return (await RepoOf<WideRecord>(check).GetAsync(id))!;
    }

    [Test]
    public async Task A_member_scoped_modify_of_twelve_members_writes_them_all()
    {
        var record = await SeedWideAsync();
        using var scope = NewScope();
        var read = (await RepoOf<WideRecord>(scope).GetAsync(record.Id))!;
        read.Fill("new", 12);
        read.Status = "not named";
        await Resolve<IPartialModifyRepository<WideRecord, string>>(scope).ModifyAsync(read, TwelveMembers);
        await Commit(scope);

        var stored = await ReadWideAsync(record.Id);
        Assert.That(stored.Members(), Is.EqualTo(new WideRecord().Fill("new", 12).Members()));
        Assert.That(stored.Status, Is.EqualTo("queued"), "a member the modify does not name keeps what the item holds");
    }

    [Test]
    public async Task A_member_scoped_modify_of_twelve_members_under_a_stale_token_writes_none()
    {
        var record = await SeedWideAsync();
        using var stale = NewScope();
        var read = (await RepoOf<WideRecord>(stale).GetAsync(record.Id))!;

        using (var other = NewScope())
        {
            var current = (await RepoOf<WideRecord>(other).GetAsync(record.Id))!;
            current.F01 = "other";
            await Resolve<IPartialModifyRepository<WideRecord, string>>(other).ModifyAsync(current, x => x.F01);
            await Commit(other);
        }

        read.Fill("late", 12);
        await Resolve<IPartialModifyRepository<WideRecord, string>>(stale).ModifyAsync(read, TwelveMembers);
        var exception = Assert.ThrowsAsync<CosmosException>(() => Commit(stale));
        Assert.That(exception!.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.PreconditionFailed));

        var stored = await ReadWideAsync(record.Id);
        Assert.That(stored.F01, Is.EqualTo("other"));
        Assert.That(stored.Members().Skip(1), Is.EqualTo(new WideRecord().Fill("seed", 0).Members().Skip(1)),
            "the patches of one item apply together or not at all");
    }

    [Test]
    public async Task A_member_scoped_modify_of_twelve_members_joins_the_transaction()
    {
        var record = await SeedWideAsync();
        using var scope = NewScope();
        var unitOfWork = Resolve<Repository.CosmosDefaultUnitOfWork>(scope);
        var read = (await RepoOf<WideRecord>(scope).GetAsync(record.Id))!;
        var job = new Job { Category = Partition, Status = "queued" };

        await unitOfWork.BeginTransactionAsync();
        read.Fill("tx", 7);
        await Resolve<IPartialModifyRepository<WideRecord, string>>(scope).ModifyAsync(read, TwelveMembers);
        await RepoOf<Job>(scope).AddAsync(job);
        await unitOfWork.CommitAsync();
        Assert.That((await ReadWideAsync(record.Id)).F01, Is.EqualTo("seed1"), "nothing is written before the transaction commits");
        await unitOfWork.CommitTransactionAsync();

        Assert.That((await ReadWideAsync(record.Id)).Members(), Is.EqualTo(new WideRecord().Fill("tx", 7).Members()));
        using var check = NewScope();
        Assert.That(await RepoOf<Job>(check).GetAsync(job.Id), Is.Not.Null);
    }

    [Test]
    public async Task A_set_based_update_of_twelve_members_writes_them_all_under_the_filter()
    {
        var record = await SeedWideAsync();
        using var scope = NewScope();
        var repo = RepoOf<WideRecord>(scope);

        Assert.That(await repo.UpdateManyAsync(x => x.Id == record.Id && x.Status == "queued", x => new WideRecord
        {
            Status = "running", F01 = "u1", F02 = "u2", F03 = "u3", F04 = "u4", F05 = "u5", F06 = "u6",
            F07 = "u7", F08 = "u8", F09 = "u9", F10 = "u10", F11 = "u11", F12 = 12,
        }), Is.EqualTo(1L));
        var stored = await ReadWideAsync(record.Id);
        Assert.That(stored.Status, Is.EqualTo("running"));
        Assert.That(stored.Members(), Is.EqualTo(new WideRecord().Fill("u", 12).Members()));

        Assert.That(await repo.UpdateManyAsync(x => x.Id == record.Id && x.Status == "queued", x => new WideRecord
        {
            Status = "again", F01 = "v1", F02 = "v2", F03 = "v3", F04 = "v4", F05 = "v5", F06 = "v6",
            F07 = "v7", F08 = "v8", F09 = "v9", F10 = "v10", F11 = "v11", F12 = 13,
        }), Is.EqualTo(0L), "the item no longer matches");
        Assert.That((await ReadWideAsync(record.Id)).Members(), Is.EqualTo(new WideRecord().Fill("u", 12).Members()));
    }

    [Test]
    public async Task A_patch_of_twelve_paths_whose_condition_fails_writes_none_of_them()
    {
        var record = await SeedWideAsync();
        var container = Resolve<Database>(NewScope()).GetContainer(CosmosTestServer.ContainerName);
        var operations = Enumerable.Range(1, 11).Select(index => PatchOperation.Set($"/f{index:00}", "x"))
            .Append(PatchOperation.Set("/f12", 99)).Append(PatchOperation.Set("/status", "taken")).ToList();

        // The item matched when it was read; by the time of the patch it does not: the first patch's condition fails.
        var exception = Assert.ThrowsAsync<CosmosException>(() => CosmosPatch.ApplyAsync<WideRecord>(container, record.Id,
            new PartitionKey(Partition), operations, "FROM root WHERE root.status = 'running'", null, CancellationToken.None));
        Assert.That(exception!.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.PreconditionFailed));
        var stored = await ReadWideAsync(record.Id);
        Assert.That(stored.Status, Is.EqualTo("queued"));
        Assert.That(stored.Members(), Is.EqualTo(new WideRecord().Fill("seed", 0).Members()), "the batch failed together");
    }

    [Test]
    public async Task Two_concurrent_set_based_updates_of_twelve_members_leave_one_whole()
    {
        var record = await SeedWideAsync();
        var workers = new[] { "a", "b" };
        var scopes = workers.Select(_ => NewScope()).ToList();
        long[] counts;
        try
        {
            counts = await Task.WhenAll(workers.Select((worker, index) => Task.Run(() =>
                RepoOf<WideRecord>(scopes[index]).UpdateManyAsync(x => x.Id == record.Id && x.Status == "queued", x => new WideRecord
                {
                    Status = worker, F01 = worker + "1", F02 = worker + "2", F03 = worker + "3", F04 = worker + "4",
                    F05 = worker + "5", F06 = worker + "6", F07 = worker + "7", F08 = worker + "8", F09 = worker + "9",
                    F10 = worker + "10", F11 = worker + "11", F12 = index,
                }))));
        }
        finally
        {
            scopes.ForEach(scope => scope.Dispose());
        }

        Assert.That(counts.OrderBy(count => count), Is.EqualTo(new[] { 0L, 1L }));
        var winner = Array.IndexOf(counts, 1L);
        var stored = await ReadWideAsync(record.Id);
        Assert.That(stored.Status, Is.EqualTo(workers[winner]));
        Assert.That(stored.Members(), Is.EqualTo(new WideRecord().Fill(workers[winner], winner).Members()), "no member of the loser");
    }

    [Test]
    public async Task UpdateAndGet_of_twelve_members_returns_the_item_as_written()
    {
        var record = await SeedWideAsync();
        using var scope = NewScope();
        var returning = Resolve<IReturningUpdateRepository<WideRecord, string>>(scope);

        var written = await returning.UpdateAndGetAsync(x => x.Id == record.Id && x.Status == "queued", x => new WideRecord
        {
            Status = "done", F01 = "r1", F02 = "r2", F03 = "r3", F04 = "r4", F05 = "r5", F06 = "r6",
            F07 = "r7", F08 = "r8", F09 = "r9", F10 = "r10", F11 = "r11", F12 = x.F12 + 5,
        });
        Assert.That(written, Has.Count.EqualTo(1));
        Assert.That(written[0].Status, Is.EqualTo("done"));
        Assert.That(written[0].Members(), Is.EqualTo(new WideRecord().Fill("r", 5).Members()), "the last patch answers the whole item");
        Assert.That((await ReadWideAsync(record.Id)).Members(), Is.EqualTo(new WideRecord().Fill("r", 5).Members()));

        Assert.That(await returning.UpdateAndGetAsync(x => x.Id == record.Id && x.Status == "queued", x => new WideRecord
        {
            Status = "x", F01 = "s1", F02 = "s2", F03 = "s3", F04 = "s4", F05 = "s5", F06 = "s6",
            F07 = "s7", F08 = "s8", F09 = "s9", F10 = "s10", F11 = "s11", F12 = 0,
        }), Is.Empty, "the condition no longer holds");
    }

    // ---------------------------------------------------------------- refusals and their exceptions

    [Test]
    public async Task A_unique_key_taken_through_the_etag_fallback_is_a_DuplicateKeyException()
    {
        var owner = new UserAccount { Category = Partition, Email = "owner@example.test" };
        var other = new UserAccount { Category = Partition, Email = "other@example.test" };
        using (var seed = NewScope())
        {
            await RepoOf<UserAccount>(seed).AddRangeAsync([owner, other]);
            await Commit(seed);
        }

        // Text compared ignoring case is a filter the store cannot evaluate as a patch's condition (it answers 400):
        // the item is patched under the _etag of a read that still matches, and that patch takes another's unique key.
        using var scope = NewScope();
        var repo = RepoOf<UserAccount>(scope);
        var exception = Assert.ThrowsAsync<DuplicateKeyException>(() => repo.UpdateManyAsync(
            x => x.Id == other.Id && x.Email.Equals("OTHER@EXAMPLE.TEST", StringComparison.OrdinalIgnoreCase),
            x => new UserAccount { Email = owner.Email }));
        Assert.That(exception!.InnerException, Is.InstanceOf<CosmosException>(), "the SDK's exception is kept");
        Assert.That((await repo.GetAsync(other.Id))!.Email, Is.EqualTo(other.Email));

        Assert.That(await repo.UpdateManyAsync(
            x => x.Id == other.Id && x.Email.Equals("OTHER@EXAMPLE.TEST", StringComparison.OrdinalIgnoreCase),
            x => new UserAccount { Email = "renamed@example.test" }), Is.EqualTo(1L), "the fallback applies an update that takes no key");
    }

    [Test]
    public async Task Deleting_many_matches_keeps_a_bounded_number_of_requests_in_flight_and_counts_every_delete()
    {
        const int stale = 200;
        using (var seed = NewScope())
        {
            await RepoOf<Job>(seed).AddRangeAsync(Enumerable.Range(0, stale).Select(_ => new Job { Category = Partition, Status = "stale" }));
            await RepoOf<Job>(seed).AddRangeAsync(Enumerable.Range(0, 3).Select(_ => new Job { Category = Partition, Status = "live" }));
            await Commit(seed);
        }

        using var scope = NewScope();
        long deleted;
        CosmosTestServer.Requests.Watch(CosmosTestServer.ContainerName);
        try
        {
            deleted = await RepoOf<Job>(scope).DeleteManyAsync(x => x.Category == Partition && x.Status == "stale");
        }
        finally
        {
            CosmosTestServer.Requests.Stop();
        }

        Assert.That(deleted, Is.EqualTo((long)stale), "every match deleted, every delete counted");
        Assert.That(CosmosTestServer.Requests.Peak, Is.InRange(2, Repository.CosmosSet<Job>.MaxConcurrentItems),
            "the items are deleted several at a time, never all of them at once");
        var left = (await RepoOf<Job>(scope).GetFilteredAsync(x => x.Category == Partition)).ToList();
        Assert.That(left.Select(x => x.Status), Is.EqualTo(new[] { "live", "live", "live" }));
    }
}
