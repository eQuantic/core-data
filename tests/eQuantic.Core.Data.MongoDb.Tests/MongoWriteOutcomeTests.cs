using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Write;
using eQuantic.Core.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>A workspace's named value, unique by workspace and name: the compound unique key upserts target.</summary>
public sealed class Secret : IEntity<string>, IEntityTimeMark, IEntityTimeTrack
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string WorkspaceId { get; set; } = "";

    public string Name { get; set; } = "";

    public string Value { get; set; } = "";

    public int Uses { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>A label unique by name among the rows not deleted: a partial unique index, where the store has them.</summary>
public sealed class Label : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

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

    public string Status { get; set; } = "";

    public string? Owner { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>A counter incremented and read in one call.</summary>
public sealed class ItemType : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public int NextSequence { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>A page two requests change member by member.</summary>
public sealed class Page : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = "";

    public bool Locked { get; set; }

    public string Body { get; set; } = "";

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>An account whose e-mail is unique under the index <c>ix_users_email</c>.</summary>
public sealed class UserAccount : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Email { get; set; } = "";

    public string Name { get; set; } = "";

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>
///     Proves, against a real MongoDB, upserts (<c>findOneAndUpdate</c>, then an upsert with <c>$setOnInsert</c>),
///     inserts skipping duplicates (an unordered <c>insertMany</c> discarding only E11000), <c>DuplicateKeyException</c>
///     on every write path, matched counts, returning updates (<c>findOneAndUpdate</c> per document) and member-scoped
///     modifies (a <c>$set</c> of the members named). The unique indexes are the ones the store holds the targets by.
/// </summary>
[TestFixture]
public sealed class MongoWriteOutcomeTests : MongoIntegrationTest
{
    private MongoTestDatabase _db = null!;

    [SetUp]
    public async Task CreateDatabase()
    {
        _db = NewDatabase();
        await _db.Database.GetCollection<BsonDocument>("Secret").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("WorkspaceId").Ascending("Name"), new CreateIndexOptions { Unique = true }));
        await _db.Database.GetCollection<BsonDocument>("UserAccount").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("Email"), new CreateIndexOptions { Unique = true, Name = "ix_users_email" }));
        await _db.Database.GetCollection<BsonDocument>("Label").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("Name"),
            new CreateIndexOptions<BsonDocument>
            {
                Unique = true,
                Name = "ux_label_live_name",
                PartialFilterExpression = new BsonDocument("DeletedAt", new BsonDocument("$type", "null")),
            }));
    }

    [TearDown]
    public void DropDatabase() => _db?.Dispose();

    private IServiceScope NewScope() => _db.NewScope();

    private static T Resolve<T>(IServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    private static IAsyncRepository<TEntity, string> Repo<TEntity>(IServiceScope scope) where TEntity : class, IEntity<string> =>
        Resolve<IAsyncRepository<TEntity, string>>(scope);

    private static Task Commit(IServiceScope scope) => Resolve<IUnitOfWork>(scope).CommitAsync();

    [Test]
    public async Task An_upsert_inserts_then_updates()
    {
        using var scope = NewScope();
        var upserts = Resolve<IUpsertRepository<Secret, string>>(scope);
        var secret = new Secret { WorkspaceId = "w", Name = "token", Value = "a" };

        var inserted = await upserts.UpsertAsync(secret);
        Assert.That(inserted.Value, Is.EqualTo("a"));
        Assert.That(inserted.CreatedAt, Is.Not.EqualTo(default(DateTime)), "the insert stamps CreatedAt");
        Assert.That(inserted.UpdatedAt, Is.Null, "an insert is not an update");

        await Task.Delay(20);
        var updated = await upserts.UpsertAsync(new Secret { Id = secret.Id, WorkspaceId = "w", Name = "token", Value = "b" });
        Assert.That(updated.Value, Is.EqualTo("b"));
        Assert.That(updated.CreatedAt, Is.EqualTo(inserted.CreatedAt).Within(TimeSpan.FromMilliseconds(1)), "the update keeps the insert's stamp");
        Assert.That(updated.UpdatedAt, Is.Not.Null, "the update stamps UpdatedAt");
        Assert.That((await Repo<Secret>(scope).GetFilteredAsync(x => x.Id == secret.Id)).Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task An_upsert_on_a_compound_unique_key_applies_the_update_and_keeps_the_first_key()
    {
        var first = new Secret { WorkspaceId = "w", Name = "token", Value = "a" };
        using var scope = NewScope();
        var upserts = (IUpsertRepository<Secret, string>)Repo<Secret>(scope);
        await upserts.UpsertAsync(first, x => new { x.WorkspaceId, x.Name }, x => new Secret { Value = "b" });
        var stored = await upserts.UpsertAsync(new Secret { WorkspaceId = "w", Name = "token", Value = "a" },
            x => new { x.WorkspaceId, x.Name }, x => new Secret { Value = "b" });

        var rows = (await Repo<Secret>(scope).GetFilteredAsync(x => x.WorkspaceId == "w" && x.Name == "token")).ToList();
        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Value, Is.EqualTo("b"));
        Assert.That(rows[0].Id, Is.EqualTo(first.Id), "the document keeps the key the first call inserted");
        Assert.That(stored.Id, Is.EqualTo(first.Id));
        Assert.That(stored.Value, Is.EqualTo("b"));
    }

    [Test]
    public async Task Ten_concurrent_upserts_of_one_target_leave_one_row_and_none_fails()
    {
        var scopes = Enumerable.Range(0, 10).Select(_ => NewScope()).ToList();
        try
        {
            await Task.WhenAll(scopes.Select(scope => Task.Run(() =>
                Resolve<IUpsertRepository<Secret, string>>(scope).UpsertAsync(
                    new Secret { WorkspaceId = "race", Name = "race", Value = "v" },
                    x => new { x.WorkspaceId, x.Name }, x => new Secret { Uses = x.Uses + 1 }))));
        }
        finally
        {
            scopes.ForEach(scope => scope.Dispose());
        }

        using var check = NewScope();
        var rows = (await Repo<Secret>(check).GetFilteredAsync(x => x.WorkspaceId == "race")).ToList();
        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Uses, Is.EqualTo(9), "one call inserted (Uses = 0) and the nine others each added one");
    }

    [Test]
    public async Task An_upsert_takes_store_generated_values_on_insert_and_never_writes_them_on_update()
    {
        using var scope = NewScope();
        var upserts = Resolve<IUpsertRepository<RunRecord, string>>(scope);
        var record = new RunRecord { State = RunState.Queued };

        var inserted = await upserts.UpsertAsync(record);
        Assert.That(inserted.Sequence, Is.GreaterThan(0), "the insert took the counter's number");
        Assert.That(inserted.RecordedAt, Is.Not.EqualTo(default(DateTime)), "the insert took the server's clock");

        var updated = await upserts.UpsertAsync(new RunRecord { Id = record.Id, State = RunState.Completed });
        Assert.That(updated.State, Is.EqualTo(RunState.Completed));
        Assert.That(updated.Sequence, Is.EqualTo(inserted.Sequence), "the update left the counter's number");
        Assert.That(updated.RecordedAt, Is.EqualTo(inserted.RecordedAt).Within(TimeSpan.FromMilliseconds(1)), "the update left the clock");

        var returned = await Resolve<IReturningUpdateRepository<RunRecord, string>>(scope)
            .UpdateAndGetAsync(x => x.Id == record.Id, x => new RunRecord { State = RunState.InProgress });
        Assert.That(returned.Single().Sequence, Is.EqualTo(inserted.Sequence), "a returning update hands back what the store assigned");
        Assert.That(returned.Single().State, Is.EqualTo(RunState.InProgress));
    }

    [Test]
    public async Task A_conflict_filter_is_the_partial_unique_index_predicate()
    {
        using var scope = NewScope();
        var repo = Repo<Label>(scope);
        var deleted = new Label { Name = "urgent", Color = "grey", DeletedAt = DateTime.UtcNow };
        await repo.AddAsync(deleted);
        await Commit(scope);

        var upserts = (IUpsertRepository<Label, string>)repo;
        var live = await upserts.UpsertAsync(new Label { Name = "urgent", Color = "red" }, x => x.Name, conflictFilter: x => x.DeletedAt == null);
        Assert.That(live.Id, Is.Not.EqualTo(deleted.Id), "the deleted document was not the target");
        var again = await upserts.UpsertAsync(new Label { Name = "urgent", Color = "blue" }, x => x.Name, conflictFilter: x => x.DeletedAt == null);
        Assert.That(again.Id, Is.EqualTo(live.Id));
        Assert.That(again.Color, Is.EqualTo("blue"));

        var rows = (await repo.GetFilteredAsync(x => x.Name == "urgent")).ToList();
        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows.Single(x => x.Id == deleted.Id).Color, Is.EqualTo("grey"));
    }

    [Test]
    public async Task Inserting_many_skips_the_rows_already_there()
    {
        var stored = new[]
        {
            new Secret { WorkspaceId = "w", Name = "one", Value = "stored" },
            new Secret { WorkspaceId = "w", Name = "two", Value = "stored" },
        };
        using (var seed = NewScope())
        {
            await Repo<Secret>(seed).AddRangeAsync(stored);
            await Commit(seed);
        }

        using var scope = NewScope();
        var inserted = await Resolve<IUpsertRepository<Secret, string>>(scope).InsertManyAsync(
        [
            new Secret { Id = stored[0].Id, WorkspaceId = "w", Name = "one-again", Value = "new" },
            new Secret { WorkspaceId = "w", Name = "two", Value = "new" },
            new Secret { WorkspaceId = "w", Name = "three", Value = "new" },
            new Secret { WorkspaceId = "w", Name = "four", Value = "new" },
            new Secret { WorkspaceId = "w", Name = "five", Value = "new" },
        ], skipDuplicates: true);

        Assert.That(inserted, Is.EqualTo(3L));
        var rows = (await Repo<Secret>(scope).GetFilteredAsync(x => x.WorkspaceId == "w")).ToList();
        Assert.That(rows.Select(x => x.Name), Is.EquivalentTo(new[] { "one", "two", "three", "four", "five" }));
        Assert.That(rows.Where(x => x.Name == "one" || x.Name == "two").Select(x => x.Value), Is.All.EqualTo("stored"));
    }

    [Test]
    public async Task Inserting_many_without_skipping_fails_on_a_taken_key()
    {
        var stored = new Secret { WorkspaceId = "w", Name = "taken", Value = "stored" };
        using (var seed = NewScope())
        {
            await Repo<Secret>(seed).AddAsync(stored);
            await Commit(seed);
        }

        using var scope = NewScope();
        var exception = Assert.ThrowsAsync<DuplicateKeyException>(() => Resolve<IUpsertRepository<Secret, string>>(scope).InsertManyAsync(
        [
            new Secret { WorkspaceId = "w", Name = "fresh", Value = "new" },
            new Secret { WorkspaceId = "w", Name = "taken", Value = "new" },
            new Secret { WorkspaceId = "w", Name = "after", Value = "new" },
        ]));
        Assert.That(exception!.InnerException, Is.InstanceOf<MongoBulkWriteException>());
        var names = (await Repo<Secret>(scope).GetFilteredAsync(x => x.WorkspaceId == "w")).Select(x => x.Name);
        Assert.That(names, Is.EquivalentTo(new[] { "taken", "fresh" }),
            "an ordered insert stops at the taken key; outside a transaction, what came before it stays written");
    }

    [Test]
    public async Task A_staged_insert_of_a_taken_key_fails_the_commit()
    {
        var stored = new Job { Status = "queued" };
        using (var seed = NewScope())
        {
            await Repo<Job>(seed).AddAsync(stored);
            await Commit(seed);
        }

        using var scope = NewScope();
        await Repo<Job>(scope).AddAsync(new Job { Id = stored.Id, Status = "again" });
        var exception = Assert.ThrowsAsync<DuplicateKeyException>(() => Commit(scope));
        Assert.That(exception!.ConstraintName, Is.EqualTo("_id_"));
        Assert.That(exception.InnerException, Is.Not.Null, "the driver's exception is kept");
        Assert.That((await Repo<Job>(scope).GetAsync(stored.Id))!.Status, Is.EqualTo("queued"));
    }

    [Test]
    public async Task A_staged_insert_of_a_taken_key_inside_a_transaction_writes_nothing()
    {
        var stored = new Job { Status = "queued" };
        using (var seed = NewScope())
        {
            await Repo<Job>(seed).AddAsync(stored);
            await Commit(seed);
        }

        using var scope = NewScope();
        var unitOfWork = Resolve<Repository.MongoDefaultUnitOfWork>(scope);
        var fresh = new Job { Status = "fresh" };
        await unitOfWork.BeginTransactionAsync();
        await Repo<Job>(scope).AddAsync(fresh);
        await Repo<Job>(scope).AddAsync(new Job { Id = stored.Id, Status = "again" });
        Assert.ThrowsAsync<DuplicateKeyException>(() => unitOfWork.CommitAsync());
        await unitOfWork.RollbackTransactionAsync();

        using var check = NewScope();
        Assert.That(await Repo<Job>(check).GetAsync(fresh.Id), Is.Null, "the transaction made the commit atomic");
    }

    [Test]
    public async Task Inserting_many_skipping_duplicates_inside_a_transaction_leaves_out_the_taken_keys()
    {
        var stored = new Secret { WorkspaceId = "w", Name = "taken", Value = "stored" };
        using (var seed = NewScope())
        {
            await Repo<Secret>(seed).AddAsync(stored);
            await Commit(seed);
        }

        using var scope = NewScope();
        var unitOfWork = Resolve<Repository.MongoDefaultUnitOfWork>(scope);
        await unitOfWork.BeginTransactionAsync();
        var inserted = await Resolve<IUpsertRepository<Secret, string>>(scope).InsertManyAsync(
        [
            new Secret { WorkspaceId = "w", Name = "fresh", Value = "new" },
            new Secret { WorkspaceId = "w", Name = "taken", Value = "new" },
            new Secret { Id = stored.Id, WorkspaceId = "w", Name = "same-id", Value = "new" },
            new Secret { WorkspaceId = "w", Name = "fresh", Value = "repeated in the batch" },
            new Secret { WorkspaceId = "w", Name = "other", Value = "new" },
        ], skipDuplicates: true);

        Assert.That(inserted, Is.EqualTo(2L), "the unique key, the id and the batch's own repeat are left out");
        Assert.That((await Repo<Secret>(scope).GetFilteredAsync(x => x.WorkspaceId == "w")).Select(x => x.Name),
            Is.EquivalentTo(new[] { "taken", "fresh", "other" }), "the transaction reads its own writes");
        using (var outside = NewScope())
        {
            Assert.That((await Repo<Secret>(outside).GetFilteredAsync(x => x.WorkspaceId == "w")).Select(x => x.Name),
                Is.EqualTo(new[] { "taken" }), "nothing is visible before the transaction commits");
        }

        await unitOfWork.CommitTransactionAsync();

        using var check = NewScope();
        var rows = (await Repo<Secret>(check).GetFilteredAsync(x => x.WorkspaceId == "w")).ToList();
        Assert.That(rows.Select(x => x.Name), Is.EquivalentTo(new[] { "taken", "fresh", "other" }));
        Assert.That(rows.Single(x => x.Name == "taken").Value, Is.EqualTo("stored"));
        Assert.That(rows.Single(x => x.Name == "fresh").Value, Is.EqualTo("new"), "the first of a repeated key is the one written");
    }

    [Test]
    public async Task Inserting_many_skipping_duplicates_inside_a_transaction_reads_a_partial_index_and_rolls_back()
    {
        using (var seed = NewScope())
        {
            await Repo<Label>(seed).AddRangeAsync(
            [
                new Label { Name = "live", Color = "stored" },
                new Label { Name = "gone", Color = "stored", DeletedAt = DateTime.UtcNow },
            ]);
            await Commit(seed);
        }

        using var scope = NewScope();
        var unitOfWork = Resolve<Repository.MongoDefaultUnitOfWork>(scope);
        await unitOfWork.BeginTransactionAsync();
        var inserted = await Resolve<IUpsertRepository<Label, string>>(scope).InsertManyAsync(
        [
            new Label { Name = "live", Color = "new" },
            new Label { Name = "gone", Color = "new" },
            new Label { Name = "live", Color = "deleted, so outside the index", DeletedAt = DateTime.UtcNow },
        ], skipDuplicates: true);
        Assert.That(inserted, Is.EqualTo(2L), "a deleted label is outside the partial index: its name does not take the live one's");
        await unitOfWork.RollbackTransactionAsync();

        using var check = NewScope();
        Assert.That(await Repo<Label>(check).GetFilteredAsync(x => x.Color == "new"), Is.Empty, "the rollback took the inserts back");
    }

    [Test]
    public async Task Inserting_many_skipping_duplicates_inside_a_transaction_compares_under_the_index_collation()
    {
        await _db.Database.GetCollection<BsonDocument>("ItemType").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("Name"),
            new CreateIndexOptions { Unique = true, Name = "ux_item_type_name", Collation = new Collation("en", strength: CollationStrength.Secondary) }));
        using (var seed = NewScope())
        {
            await Repo<ItemType>(seed).AddAsync(new ItemType { Name = "bug" });
            await Commit(seed);
        }

        using var scope = NewScope();
        var unitOfWork = Resolve<Repository.MongoDefaultUnitOfWork>(scope);
        await unitOfWork.BeginTransactionAsync();
        var inserted = await Resolve<IUpsertRepository<ItemType, string>>(scope).InsertManyAsync(
        [
            new ItemType { Name = "BUG" },
            new ItemType { Name = "Task" },
            new ItemType { Name = "TASK" },
            new ItemType { Name = "Epic" },
        ], skipDuplicates: true);
        await unitOfWork.CommitTransactionAsync();

        Assert.That(inserted, Is.EqualTo(2L));
        using var check = NewScope();
        Assert.That((await Repo<ItemType>(check).GetAllAsync()).Select(x => x.Name), Is.EquivalentTo(new[] { "bug", "Task", "Epic" }));
    }

    [Test]
    public async Task A_unique_index_other_than_the_key_is_named_on_every_write_path()
    {
        var owner = new UserAccount { Email = "owner@example.test", Name = "owner" };
        var other = new UserAccount { Email = "other@example.test", Name = "other" };
        using (var seed = NewScope())
        {
            await Repo<UserAccount>(seed).AddRangeAsync([owner, other]);
            await Commit(seed);
        }

        using var scope = NewScope();
        var repo = Repo<UserAccount>(scope);
        await repo.AddAsync(new UserAccount { Email = owner.Email, Name = "staged" });
        Assert.That(Assert.ThrowsAsync<DuplicateKeyException>(() => Commit(scope))!.ConstraintName, Is.EqualTo("ix_users_email"),
            "a staged insert");

        var upserts = (IUpsertRepository<UserAccount, string>)repo;
        Assert.That(Assert.ThrowsAsync<DuplicateKeyException>(() => upserts.InsertManyAsync([new UserAccount { Email = owner.Email }]))!
            .ConstraintName, Is.EqualTo("ix_users_email"), "a batch insert");
        Assert.That(Assert.ThrowsAsync<DuplicateKeyException>(() =>
                repo.UpdateManyAsync(x => x.Id == other.Id, x => new UserAccount { Email = owner.Email }))!
            .ConstraintName, Is.EqualTo("ix_users_email"), "a set-based update");
        Assert.That(Assert.ThrowsAsync<DuplicateKeyException>(() =>
                upserts.UpsertAsync(new UserAccount { Email = owner.Email, Name = "upserted" }))!
            .ConstraintName, Is.EqualTo("ix_users_email"), "an upsert on the key that takes another unique key");
    }

    [Test]
    public async Task Two_concurrent_compare_and_sets_match_once()
    {
        var job = new Job { Status = "queued" };
        using (var seed = NewScope())
        {
            await Repo<Job>(seed).AddAsync(job);
            await Commit(seed);
        }

        var workers = new[] { "worker-a", "worker-b" };
        var scopes = workers.Select(_ => NewScope()).ToList();
        long[] counts;
        try
        {
            counts = await Task.WhenAll(workers.Select((worker, index) => Task.Run(() =>
                Repo<Job>(scopes[index]).UpdateManyAsync(x => x.Id == job.Id && x.Status == "queued",
                    x => new Job { Status = "running", Owner = worker }))));
        }
        finally
        {
            scopes.ForEach(scope => scope.Dispose());
        }

        Assert.That(counts.OrderBy(count => count), Is.EqualTo(new[] { 0L, 1L }));
        using var check = NewScope();
        var claimed = (await Repo<Job>(check).GetAsync(job.Id))!;
        Assert.That(claimed.Status, Is.EqualTo("running"));
        Assert.That(claimed.Owner, Is.EqualTo(workers[Array.IndexOf(counts, 1L)]), "the document is the winner's");
    }

    [Test]
    public async Task A_set_based_write_counts_the_documents_it_matched_not_the_ones_it_modified()
    {
        var job = new Job { Status = "running" };
        using var scope = NewScope();
        await Repo<Job>(scope).AddAsync(job);
        await Commit(scope);

        Assert.That(await Repo<Job>(scope).UpdateManyAsync(x => x.Id == job.Id, x => new Job { Status = "running" }), Is.EqualTo(1L),
            "matched, not modified");
        Assert.That(await Repo<Job>(scope).DeleteManyAsync(x => x.Id == job.Id && x.Status == "queued"), Is.EqualTo(0L));
        Assert.That(await Repo<Job>(scope).DeleteManyAsync(x => x.Id == job.Id && x.Status == "running"), Is.EqualTo(1L));
    }

    [Test]
    public async Task Two_concurrent_increments_read_their_own_values()
    {
        var itemType = new ItemType { Name = "task", NextSequence = 7 };
        using (var seed = NewScope())
        {
            await Repo<ItemType>(seed).AddAsync(itemType);
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
        Assert.That((await Repo<ItemType>(check).GetAsync(itemType.Id))!.NextSequence, Is.EqualTo(9));
        Assert.That(await Resolve<IReturningUpdateRepository<ItemType, string>>(check)
            .UpdateAndGetAsync(x => x.Id == itemType.Id && x.NextSequence == 100, x => new ItemType { NextSequence = 0 }), Is.Empty);
    }

    [Test]
    public async Task A_member_scoped_modify_leaves_the_other_members()
    {
        var page = new Page { Title = "draft", Body = "text" };
        using (var seed = NewScope())
        {
            await Repo<Page>(seed).AddAsync(page);
            await Commit(seed);
        }

        using var first = NewScope();
        using var second = NewScope();
        var titled = (await Repo<Page>(first).GetAsync(page.Id))!;
        var locked = (await Repo<Page>(second).GetAsync(page.Id))!;
        titled.Title = "final";
        locked.Locked = true;
        await Resolve<IPartialModifyRepository<Page, string>>(first).ModifyAsync(titled, x => x.Title);
        ((IPartialModifyRepository<Page, string>)Repo<Page>(second)).Modify(locked, x => new { x.Locked });
        await Commit(first);
        await Commit(second);

        using var check = NewScope();
        var stored = (await Repo<Page>(check).GetAsync(page.Id))!;
        Assert.That(stored.Title, Is.EqualTo("final"));
        Assert.That(stored.Locked, Is.True);
        Assert.That(stored.Body, Is.EqualTo("text"));
    }
}
