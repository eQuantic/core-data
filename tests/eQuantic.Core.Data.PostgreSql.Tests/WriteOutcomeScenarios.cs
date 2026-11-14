using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Relational.Repository;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Write;
using eQuantic.Core.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.PostgreSql.Tests;

/// <summary>A workspace's named value, unique by workspace and name: the compound unique key upserts target.</summary>
public sealed class Secret : IEntity<Guid>, IEntityTimeMark, IEntityTimeTrack
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkspaceId { get; set; }

    public string Name { get; set; } = "";

    public string Value { get; set; } = "";

    public int Uses { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A label unique by name among the rows not deleted: a partial unique index, where the store has them.</summary>
public sealed class Label : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public string Color { get; set; } = "";

    public DateTime? DeletedAt { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A queued job a worker claims with a compare-and-set.</summary>
public sealed class Job : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Status { get; set; } = "";

    public string? Owner { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A counter incremented and read in one call.</summary>
public sealed class ItemType : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

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

/// <summary>An account whose e-mail is unique under the index <c>ix_users_email</c>.</summary>
public sealed class UserAccount : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Email { get; set; } = "";

    public string Name { get; set; } = "";

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A sprint the store numbers: an <c>int</c> identity key an upsert targets.</summary>
public sealed class Sprint : IEntity<int>
{
    public int Id { get; set; }

    public string Board { get; set; } = "";

    public string Goal { get; set; } = "";

    public int GetKey() => Id;

    public void SetKey(int key) => Id = key;
}

/// <summary>A runner unique by name among the active ones: a partial unique index whose predicate holds a value.</summary>
public sealed class Runner : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public string State { get; set; } = "";

    public string Host { get; set; } = "";

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>The write-outcome entities' mapping and migration, shared by every relational suite.</summary>
internal static class WriteOutcomeSchema
{
    public static Relational.RelationalModelBuilder Configure(Relational.RelationalModelBuilder builder) => builder
        .Entity<Secret>(_ => { })
        .Entity<Label>(_ => { })
        .Entity<Job>(_ => { })
        .Entity<ItemType>(_ => { })
        .Entity<Page>(_ => { })
        .Entity<UserAccount>(entity => entity.Table("users"))
        .Entity<Sprint>(entity => entity.Key(x => x.Id, generated: true))
        .Entity<Runner>(_ => { });

    /// <summary>The tables and their unique indexes; a partial one only where the store has them.</summary>
    public static IMigrationBuilder Migrate(IMigrationBuilder migration, bool partialIndexes) => migration
        .For<Secret>(secret => secret
            .EnsureCollection()
            .CompositeIndex(keys => keys.Ascending(x => x.WorkspaceId).Ascending(x => x.Name), unique: true))
        .For<Label>(label =>
        {
            label.EnsureCollection();
            if (partialIndexes)
            {
                label.Index(x => x.Name, options => options.Unique().Filtered(x => x.DeletedAt == null).Named("ux_label_live_name"));
            }
        })
        .For<Job>(job => job.EnsureCollection())
        .For<ItemType>(itemType => itemType.EnsureCollection())
        .For<Page>(page => page.EnsureCollection())
        .For<UserAccount>(user => user
            .EnsureCollection()
            .Index(x => x.Email, options => options.Unique().Named("ix_users_email")))
        .For<Sprint>(sprint => sprint.EnsureCollection())
        .For<Runner>(runner =>
        {
            runner.EnsureCollection();
            if (partialIndexes)
            {
                runner.Index(x => x.Name, options => options.Unique().Filtered(x => x.State == "active").Named("ux_runner_active_name"));
            }
        });
}

/// <summary>
///     The scenarios of the upsert and write-outcome requirements, each run against a real store: every call goes
///     through a repository resolved from its own DI scope — a unit of work of its own — and the races run them at
///     the same time.
/// </summary>
internal static class WriteOutcomeScenarios
{
    private static T Resolve<T>(IServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    private static IAsyncRepository<TEntity, Guid> Repo<TEntity>(IServiceScope scope) where TEntity : class, IEntity<Guid> =>
        Resolve<IAsyncRepository<TEntity, Guid>>(scope);

    private static Task Commit(IServiceScope scope) => Resolve<IUnitOfWork>(scope).CommitAsync();

    /// <summary>An upsert of an entity whose key no row has inserts it; again with a changed member, it updates that row.</summary>
    public static async Task UpsertInsertsThenUpdates(Func<IServiceScope> newScope)
    {
        using var scope = newScope();
        var upserts = Resolve<IUpsertRepository<Secret, Guid>>(scope);
        var secret = new Secret { WorkspaceId = Guid.NewGuid(), Name = "token", Value = "a" };

        var inserted = await upserts.UpsertAsync(secret);
        Assert.That(inserted.Id, Is.EqualTo(secret.Id));
        Assert.That(inserted.Value, Is.EqualTo("a"));
        Assert.That(inserted.CreatedAt, Is.Not.EqualTo(default(DateTime)), "the insert stamps CreatedAt");
        Assert.That(inserted.UpdatedAt, Is.Null, "an insert is not an update");

        await Task.Delay(20);
        var changed = new Secret { Id = secret.Id, WorkspaceId = secret.WorkspaceId, Name = "token", Value = "b" };
        var updated = await upserts.UpsertAsync(changed);
        Assert.That(updated.Value, Is.EqualTo("b"));
        Assert.That(updated.CreatedAt, Is.EqualTo(inserted.CreatedAt).Within(TimeSpan.FromMilliseconds(1)),
            "the update keeps the insert's stamp");
        Assert.That(updated.UpdatedAt, Is.Not.Null, "the update stamps UpdatedAt");

        var rows = await Repo<Secret>(scope).GetFilteredAsync(x => x.Id == secret.Id);
        Assert.That(rows.Count(), Is.EqualTo(1));
    }

    /// <summary>Twice on a compound unique key with an update expression: one row, the second call's value, the first call's key.</summary>
    public static async Task UpsertOnCompoundUniqueKeyWithUpdate(Func<IServiceScope> newScope)
    {
        var workspace = Guid.NewGuid();
        var first = new Secret { WorkspaceId = workspace, Name = "token", Value = "a" };
        var second = new Secret { WorkspaceId = workspace, Name = "token", Value = "a" };

        using var scope = newScope();
        var upserts = (IUpsertRepository<Secret, Guid>)Repo<Secret>(scope);
        await upserts.UpsertAsync(first, x => new { x.WorkspaceId, x.Name }, x => new Secret { Value = "b" });
        var stored = await upserts.UpsertAsync(second, x => new { x.WorkspaceId, x.Name }, x => new Secret { Value = "b" });

        var rows = (await Repo<Secret>(scope).GetFilteredAsync(x => x.WorkspaceId == workspace && x.Name == "token")).ToList();
        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Value, Is.EqualTo("b"));
        Assert.That(rows[0].Id, Is.EqualTo(first.Id), "the row keeps the key the first call inserted");
        Assert.That(stored.Id, Is.EqualTo(first.Id), "the call returns the stored row");
        Assert.That(stored.Value, Is.EqualTo("b"));
    }

    /// <summary>Ten upserts of one target at the same time: one row, no call fails, and each call either inserted or updated.</summary>
    public static async Task UpsertRace(Func<IServiceScope> newScope)
    {
        var workspace = Guid.NewGuid();
        var scopes = Enumerable.Range(0, 10).Select(_ => newScope()).ToList();
        try
        {
            await Task.WhenAll(scopes.Select(scope => Task.Run(() =>
                Resolve<IUpsertRepository<Secret, Guid>>(scope).UpsertAsync(
                    new Secret { WorkspaceId = workspace, Name = "race", Value = "v" },
                    x => new { x.WorkspaceId, x.Name }, x => new Secret { Uses = x.Uses + 1 }))));
        }
        finally
        {
            scopes.ForEach(scope => scope.Dispose());
        }

        using var check = newScope();
        var rows = (await Repo<Secret>(check).GetFilteredAsync(x => x.WorkspaceId == workspace)).ToList();
        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Uses, Is.EqualTo(9), "one call inserted (Uses = 0) and the nine others each added one");
    }

    /// <summary>An upsert's insert gets the store's values for store-generated members; its update never writes them.</summary>
    public static async Task UpsertKeepsStoreGeneratedMembers(Func<IServiceScope> newScope)
    {
        using var scope = newScope();
        var upserts = Resolve<IUpsertRepository<RunRecord, Guid>>(scope);
        var record = new RunRecord { State = RunState.Queued };

        var inserted = await upserts.UpsertAsync(record);
        Assert.That(inserted.Sequence, Is.GreaterThan(0), "the insert took the sequence's number");
        Assert.That(inserted.RecordedAt, Is.Not.EqualTo(default(DateTime)), "the insert took the store's clock");

        var updated = await upserts.UpsertAsync(new RunRecord { Id = record.Id, State = RunState.Completed });
        Assert.That(updated.State, Is.EqualTo(RunState.Completed));
        Assert.That(updated.Sequence, Is.EqualTo(inserted.Sequence), "the update left the sequence's number");
        Assert.That(updated.RecordedAt, Is.EqualTo(inserted.RecordedAt).Within(TimeSpan.FromMilliseconds(1)),
            "the update left the store's clock");

        var returned = await Resolve<IReturningUpdateRepository<RunRecord, Guid>>(scope)
            .UpdateAndGetAsync(x => x.Id == record.Id, x => new RunRecord { State = RunState.InProgress });
        Assert.That(returned.Single().Sequence, Is.EqualTo(inserted.Sequence), "a returning update hands back what the store assigned");
        Assert.That(returned.Single().State, Is.EqualTo(RunState.InProgress));
    }

    /// <summary>
    ///     A conflict filter restricts the target to the rows it matches: a deleted row with the name is not the target,
    ///     so the first upsert inserts beside it, and the second updates the live one.
    /// </summary>
    public static async Task UpsertWithConflictFilter(Func<IServiceScope> newScope)
    {
        var name = "label-" + Guid.NewGuid().ToString("N")[..8];
        using var scope = newScope();
        var repo = Repo<Label>(scope);
        var deleted = new Label { Name = name, Color = "grey", DeletedAt = DateTime.UtcNow };
        await repo.AddAsync(deleted);
        await Commit(scope);

        var upserts = (IUpsertRepository<Label, Guid>)repo;
        var live = await upserts.UpsertAsync(new Label { Name = name, Color = "red" }, x => x.Name, conflictFilter: x => x.DeletedAt == null);
        Assert.That(live.Id, Is.Not.EqualTo(deleted.Id), "the deleted row was not the target");

        var again = await upserts.UpsertAsync(new Label { Name = name, Color = "blue" }, x => x.Name, conflictFilter: x => x.DeletedAt == null);
        Assert.That(again.Id, Is.EqualTo(live.Id));
        Assert.That(again.Color, Is.EqualTo("blue"));

        var rows = (await repo.GetFilteredAsync(x => x.Name == name)).ToList();
        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows.Single(x => x.Id == deleted.Id).Color, Is.EqualTo("grey"), "the deleted row is untouched");
    }

    /// <summary>Two of five entities share keys with stored rows: the batch inserts three and leaves the two stored rows as they were.</summary>
    public static async Task InsertManySkipsDuplicates(Func<IServiceScope> newScope)
    {
        var workspace = Guid.NewGuid();
        var stored = new[]
        {
            new Secret { WorkspaceId = workspace, Name = "one", Value = "stored" },
            new Secret { WorkspaceId = workspace, Name = "two", Value = "stored" },
        };
        using (var seed = newScope())
        {
            await Repo<Secret>(seed).AddRangeAsync(stored);
            await Commit(seed);
        }

        using var scope = newScope();
        var batch = new[]
        {
            new Secret { Id = stored[0].Id, WorkspaceId = workspace, Name = "one-again", Value = "new" },
            new Secret { WorkspaceId = workspace, Name = "two", Value = "new" },
            new Secret { WorkspaceId = workspace, Name = "three", Value = "new" },
            new Secret { WorkspaceId = workspace, Name = "four", Value = "new" },
            new Secret { WorkspaceId = workspace, Name = "five", Value = "new" },
        };

        var inserted = await Resolve<IUpsertRepository<Secret, Guid>>(scope).InsertManyAsync(batch, skipDuplicates: true);

        Assert.That(inserted, Is.EqualTo(3L));
        var rows = (await Repo<Secret>(scope).GetFilteredAsync(x => x.WorkspaceId == workspace)).ToList();
        Assert.That(rows.Select(x => x.Name), Is.EquivalentTo(new[] { "one", "two", "three", "four", "five" }));
        Assert.That(rows.Where(x => x.Name is "one" or "two").Select(x => x.Value), Is.All.EqualTo("stored"),
            "the two stored rows are unchanged");
    }

    /// <summary>Without skipping, a taken key fails the batch — and nothing of it is written.</summary>
    public static async Task InsertManyWithoutSkippingFails(Func<IServiceScope> newScope)
    {
        var workspace = Guid.NewGuid();
        var stored = new Secret { WorkspaceId = workspace, Name = "taken", Value = "stored" };
        using (var seed = newScope())
        {
            await Repo<Secret>(seed).AddAsync(stored);
            await Commit(seed);
        }

        using var scope = newScope();
        var batch = new[]
        {
            new Secret { WorkspaceId = workspace, Name = "fresh", Value = "new" },
            new Secret { WorkspaceId = workspace, Name = "taken", Value = "new" },
        };

        Assert.ThrowsAsync<DuplicateKeyException>(() =>
            Resolve<IUpsertRepository<Secret, Guid>>(scope).InsertManyAsync(batch));
        var rows = await Repo<Secret>(scope).GetFilteredAsync(x => x.WorkspaceId == workspace);
        Assert.That(rows.Select(x => x.Name), Is.EqualTo(new[] { "taken" }), "the batch is one transaction");
    }

    /// <summary>A staged insert of a taken key fails the commit with DuplicateKeyException, and the commit writes nothing.</summary>
    public static async Task StagedInsertOfTakenKeyFailsTheCommit(Func<IServiceScope> newScope)
    {
        var stored = new Job { Status = "queued" };
        using (var seed = newScope())
        {
            await Repo<Job>(seed).AddAsync(stored);
            await Commit(seed);
        }

        using var scope = newScope();
        var fresh = new Job { Status = "fresh" };
        await Repo<Job>(scope).AddAsync(fresh);
        await Repo<Job>(scope).AddAsync(new Job { Id = stored.Id, Status = "again" });

        var exception = Assert.ThrowsAsync<DuplicateKeyException>(() => Commit(scope));
        Assert.That(exception!.InnerException, Is.Not.Null, "the driver's exception is kept");
        Assert.That(await Repo<Job>(scope).GetAsync(fresh.Id), Is.Null, "the commit is atomic: nothing of it was written");
        Assert.That((await Repo<Job>(scope).GetAsync(stored.Id))!.Status, Is.EqualTo("queued"));
    }

    /// <summary>A unique index other than the key names itself, on every write path.</summary>
    public static async Task UniqueIndexIsNamed(Func<IServiceScope> newScope)
    {
        var email = $"{Guid.NewGuid():N}@example.test";
        var owner = new UserAccount { Email = email, Name = "owner" };
        var other = new UserAccount { Email = "other-" + email, Name = "other" };
        using (var seed = newScope())
        {
            await Repo<UserAccount>(seed).AddRangeAsync([owner, other]);
            await Commit(seed);
        }

        using var scope = newScope();
        var repo = Repo<UserAccount>(scope);
        await repo.AddAsync(new UserAccount { Email = email, Name = "staged" });
        Taken(() => Commit(scope), "a staged insert");

        var upserts = (IUpsertRepository<UserAccount, Guid>)repo;
        Taken(() => upserts.InsertManyAsync([new UserAccount { Email = email }]), "a batch insert");
        Taken(() => repo.UpdateManyAsync(x => x.Id == other.Id, x => new UserAccount { Email = email }), "a set-based update");
        Taken(() => upserts.UpsertAsync(new UserAccount { Email = email, Name = "upserted" }),
            "an upsert on the key that takes another unique key");

        static void Taken(AsyncTestDelegate write, string path)
        {
            var exception = Assert.ThrowsAsync<DuplicateKeyException>(write, path)!;
            Assert.That(exception.ConstraintName, Is.EqualTo("ix_users_email"), path);
            Assert.That(exception.InnerException, Is.Not.Null.And.Not.InstanceOf<DuplicateKeyException>(),
                $"{path}: the inner exception is the driver's own, not another DuplicateKeyException");
        }
    }

    /// <summary>Two compare-and-sets of one queued job at the same time: one matches, the other matches nothing, and the row is the winner's.</summary>
    public static async Task CompareAndSet(Func<IServiceScope> newScope)
    {
        var job = new Job { Status = "queued" };
        using (var seed = newScope())
        {
            await Repo<Job>(seed).AddAsync(job);
            await Commit(seed);
        }

        var workers = new[] { "worker-a", "worker-b" };
        var scopes = workers.Select(_ => newScope()).ToList();
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
        using var check = newScope();
        var claimed = (await Repo<Job>(check).GetAsync(job.Id))!;
        Assert.That(claimed.Status, Is.EqualTo("running"));
        Assert.That(claimed.Owner, Is.EqualTo(workers[Array.IndexOf(counts, 1L)]), "the row is the winner's");
    }

    /// <summary>An update that writes the value a matching row already holds still matched it.</summary>
    public static async Task MatchedNotModified(Func<IServiceScope> newScope)
    {
        var job = new Job { Status = "running" };
        using var scope = newScope();
        await Repo<Job>(scope).AddAsync(job);
        await Commit(scope);

        var count = await Repo<Job>(scope).UpdateManyAsync(x => x.Id == job.Id, x => new Job { Status = "running" });
        Assert.That(count, Is.EqualTo(1L));
        Assert.That(await Repo<Job>(scope).DeleteManyAsync(x => x.Id == job.Id && x.Status == "queued"), Is.EqualTo(0L),
            "a delete whose condition no longer holds matches nothing");
        Assert.That(await Repo<Job>(scope).DeleteManyAsync(x => x.Id == job.Id && x.Status == "running"), Is.EqualTo(1L));
    }

    /// <summary>Two increments-and-reads of one counter at the same time: one sees 8, the other 9, and the row holds 9.</summary>
    public static async Task IncrementAndRead(Func<IServiceScope> newScope)
    {
        var itemType = new ItemType { Name = "task", NextSequence = 7 };
        using (var seed = newScope())
        {
            await Repo<ItemType>(seed).AddAsync(itemType);
            await Commit(seed);
        }

        var scopes = Enumerable.Range(0, 2).Select(_ => newScope()).ToList();
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
        using var check = newScope();
        Assert.That((await Repo<ItemType>(check).GetAsync(itemType.Id))!.NextSequence, Is.EqualTo(9));

        var none = await Resolve<IReturningUpdateRepository<ItemType, Guid>>(check)
            .UpdateAndGetAsync(x => x.Id == itemType.Id && x.NextSequence == 100, x => new ItemType { NextSequence = 0 });
        Assert.That(none, Is.Empty, "a row the filter does not match is neither updated nor returned");
    }

    /// <summary>Two units of work read one row; one modifies only the title, the other only the lock, naming them: both changes stay.</summary>
    public static async Task PartialModifyKeepsOtherMembers(Func<IServiceScope> newScope)
    {
        var page = new Page { Title = "draft", Body = "text" };
        using (var seed = newScope())
        {
            await Repo<Page>(seed).AddAsync(page);
            await Commit(seed);
        }

        using var first = newScope();
        using var second = newScope();
        var titled = (await Repo<Page>(first).GetAsync(page.Id))!;
        var locked = (await Repo<Page>(second).GetAsync(page.Id))!;

        titled.Title = "final";
        locked.Locked = true;
        await Resolve<IPartialModifyRepository<Page, Guid>>(first).ModifyAsync(titled, x => x.Title);
        ((IPartialModifyRepository<Page, Guid>)Repo<Page>(second)).Modify(locked, x => new { x.Locked });
        await Commit(first);
        await Commit(second);

        using var check = newScope();
        var stored = (await Repo<Page>(check).GetAsync(page.Id))!;
        Assert.That(stored.Title, Is.EqualTo("final"));
        Assert.That(stored.Locked, Is.True);
        Assert.That(stored.Body, Is.EqualTo("text"));
    }

    /// <summary>
    ///     An upsert on a key the store generates: the row holding the entity's key is updated, and a key no row holds
    ///     inserts a row the store numbers, as an insert of a new entity does.
    /// </summary>
    public static async Task UpsertOnGeneratedKey(Func<IServiceScope> newScope)
    {
        using var scope = newScope();
        var upserts = Resolve<IUpsertRepository<Sprint, int>>(scope);
        var board = "board-" + Guid.NewGuid().ToString("N");

        var inserted = await upserts.UpsertAsync(new Sprint { Board = board, Goal = "first" });
        Assert.That(inserted.Id, Is.GreaterThan(0), "a new entity takes the store's number");

        var updated = await upserts.UpsertAsync(new Sprint { Id = inserted.Id, Board = board, Goal = "second" });
        Assert.That(updated.Id, Is.EqualTo(inserted.Id), "the row holding the key is the target");
        Assert.That(updated.Goal, Is.EqualTo("second"));

        var unknown = await upserts.UpsertAsync(new Sprint { Id = int.MaxValue, Board = board, Goal = "third" });
        Assert.That(unknown.Id, Is.Not.EqualTo(int.MaxValue).And.Not.EqualTo(inserted.Id),
            "a key no row holds is not written: the store numbers the row");

        var rows = (await Resolve<IAsyncRepository<Sprint, int>>(scope).GetFilteredAsync(x => x.Board == board))
            .OrderBy(x => x.Id).ToList();
        Assert.That(rows.Select(x => (x.Id, x.Goal)), Is.EqualTo(new[] { (inserted.Id, "second"), (unknown.Id, "third") }),
            "the upsert of the stored key updated its row instead of inserting another");
    }

    /// <summary>A store that cannot hand back a key it generates refuses an upsert on one, before writing anything.</summary>
    public static async Task UpsertOnGeneratedKeyIsRefused(Func<IServiceScope> newScope)
    {
        using var scope = newScope();
        var board = "board-" + Guid.NewGuid().ToString("N");
        Assert.ThrowsAsync<NotSupportedException>(() =>
            Resolve<IUpsertRepository<Sprint, int>>(scope).UpsertAsync(new Sprint { Id = 1, Board = board, Goal = "first" }));
        Assert.That(await Resolve<IAsyncRepository<Sprint, int>>(scope).GetFilteredAsync(x => x.Board == board), Is.Empty);
    }

    /// <summary>
    ///     Twenty-five upserts of one runner through one unit of work, past the executions after which the driver prepares the
    ///     statement and the store may plan it once for any value: each upsert after the first updates the active runner,
    ///     because the conflict filter names the partial index's predicate every time.
    /// </summary>
    public static async Task ConflictFilterWithAValue(Func<IServiceScope> newScope)
    {
        var name = "runner-" + Guid.NewGuid().ToString("N")[..8];
        using var scope = newScope();
        var retired = new Runner { Name = name, State = "retired", Host = "old" };
        await Repo<Runner>(scope).AddAsync(retired);
        await Commit(scope);

        var upserts = Resolve<IUpsertRepository<Runner, Guid>>(scope);
        var ids = new HashSet<Guid>();
        for (var round = 0; round < 25; round++)
        {
            var stored = await upserts.UpsertAsync(new Runner { Name = name, State = "active", Host = "host-" + round },
                x => x.Name, conflictFilter: x => x.State == "active");
            ids.Add(stored.Id);
        }

        Assert.That(ids, Has.Count.EqualTo(1), "one active runner, inserted once and updated after");
        var rows = (await Repo<Runner>(scope).GetFilteredAsync(x => x.Name == name)).ToList();
        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows.Single(x => x.State == "active").Host, Is.EqualTo("host-24"));
        Assert.That(rows.Single(x => x.Id == retired.Id).Host, Is.EqualTo("old"), "the retired runner is not the target");
    }

    /// <summary>
    ///     Two upserts of one target at the same time, each inside an explicit transaction of its own, round after round: one
    ///     inserts and the other updates, and neither fails, whether the target is not there yet or already is.
    /// </summary>
    public static async Task UpsertsInExplicitTransactions(Func<IServiceScope> newScope)
    {
        for (var round = 0; round < 20; round++)
        {
            var workspace = Guid.NewGuid();
            await RaceAsync(workspace);
            await RaceAsync(workspace);

            using var check = newScope();
            var rows = (await Repo<Secret>(check).GetFilteredAsync(x => x.WorkspaceId == workspace)).ToList();
            Assert.That(rows, Has.Count.EqualTo(1), $"round {round}: one row");
            Assert.That(rows[0].Uses, Is.EqualTo(3), $"round {round}: one insert (Uses = 0) and three updates");
        }

        async Task RaceAsync(Guid workspace)
        {
            var scopes = Enumerable.Range(0, 2).Select(_ => newScope()).ToList();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                var racers = scopes.Select(scope => Task.Run(async () =>
                {
                    var unitOfWork = (RelationalUnitOfWork)Resolve<IUnitOfWork>(scope);
                    await unitOfWork.BeginTransactionAsync();
                    await start.Task;
                    await Resolve<IUpsertRepository<Secret, Guid>>(scope).UpsertAsync(
                        new Secret { WorkspaceId = workspace, Name = "race", Value = "v" },
                        x => new { x.WorkspaceId, x.Name }, x => new Secret { Uses = x.Uses + 1 });
                    await unitOfWork.CommitTransactionAsync();
                })).ToList();
                start.SetResult();
                await Task.WhenAll(racers);
            }
            finally
            {
                scopes.ForEach(scope => scope.Dispose());
            }
        }
    }

    /// <summary>A bulk load of a taken key fails with DuplicateKeyException.</summary>
    public static async Task BulkInsertOfTakenKeyFails(Func<IServiceScope> newScope)
    {
        var stored = new Job { Status = "queued" };
        using var scope = newScope();
        await Repo<Job>(scope).AddAsync(stored);
        await Commit(scope);

        var unitOfWork = (RelationalUnitOfWork)Resolve<IUnitOfWork>(scope);
        Assert.ThrowsAsync<DuplicateKeyException>(() =>
            unitOfWork.BulkInsertAsync([new Job { Status = "fresh" }, new Job { Id = stored.Id, Status = "again" }]));
        Assert.That((await Repo<Job>(scope).GetFilteredAsync(x => x.Status == "fresh")).Count(), Is.EqualTo(0),
            "the load is atomic");
    }
}
