using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Read;
using eQuantic.Core.Data.Repository.Write;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.SqlServer.Tests;

/// <summary>An item with its mentions and runs (collections) and the box it may be in (a reference).</summary>
public sealed class RelItem : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = "";

    public Guid? BoxId { get; set; }

    public RelBox? Box { get; set; }

    public List<RelMention>? Mentions { get; set; }

    public List<RelRun>? Runs { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A user mentioned in an item, soft-deleted by a date the filter tests.</summary>
public sealed class RelMention : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RelItemId { get; set; }

    public Guid UserId { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A run of an item.</summary>
public sealed class RelRun : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RelItemId { get; set; }

    public string Status { get; set; } = "";

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>The box an item may be in.</summary>
public sealed class RelBox : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>An artifact of a repository (a reference whose members the filter reads, and through it the repository's space).</summary>
public sealed class RelArtifact : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RepoId { get; set; }

    public RelRepo? Repo { get; set; }

    public string Name { get; set; } = "";

    public int Downloads { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A repository of a workspace, in a space.</summary>
public sealed class RelRepo : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkspaceId { get; set; }

    public Guid? SpaceId { get; set; }

    public RelSpace? Space { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A space of a workspace.</summary>
public sealed class RelSpace : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkspaceId { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A company whose name is matched without regard to case.</summary>
public sealed class RelCompany : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public int Rank { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A node of a tree: its children are rows of its own table, which a filter across them reads.</summary>
public sealed class RelNode : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? ParentId { get; set; }

    public string Name { get; set; } = "";

    public int Rank { get; set; }

    public List<RelNode>? Children { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>The query-predicate entities' mapping: the navigations follow the conventions (<c>{Nav}Id</c>, <c>{Owner}Id</c>).</summary>
internal static class QueryPredicateSchema
{
    public static Relational.RelationalModelBuilder Configure(Relational.RelationalModelBuilder builder) => builder
        .Entity<RelItem>(_ => { })
        .Entity<RelMention>(_ => { })
        .Entity<RelRun>(_ => { })
        .Entity<RelBox>(_ => { })
        .Entity<RelArtifact>(_ => { })
        .Entity<RelRepo>(_ => { })
        .Entity<RelSpace>(_ => { })
        .Entity<RelCompany>(_ => { })
        .Entity<RelNode>(entity => entity.Collection(x => x.Children, child => child.ParentId));
}

/// <summary>Creates the query-predicate tables.</summary>
[Migration("Query predicates", 2026, 1, 6, 0, 0, 0)]
public sealed class QueryPredicateMigration : Data.Migration.Migration
{
    /// <inheritdoc />
    public override void Up(IMigrationBuilder migration) => migration
        .For<RelItem>(item => item.EnsureCollection())
        .For<RelMention>(mention => mention.EnsureCollection().Index(x => x.RelItemId))
        .For<RelRun>(run => run.EnsureCollection().Index(x => x.RelItemId))
        .For<RelBox>(box => box.EnsureCollection())
        .For<RelArtifact>(artifact => artifact.EnsureCollection().Index(x => x.RepoId))
        .For<RelRepo>(repo => repo.EnsureCollection())
        .For<RelSpace>(space => space.EnsureCollection())
        .For<RelCompany>(company => company.EnsureCollection())
        .For<RelNode>(node => node.EnsureCollection().Index(x => x.ParentId));
}

/// <summary>
///     The query-predicate scenarios every relational dialect passes — filters across relations (a correlated
///     <c>EXISTS</c>) in reads, counts and set-based writes, text matched without regard to case, and an array or a list
///     tested with <c>Contains</c>.
/// </summary>
internal static class QueryPredicateScenarios
{
    private static IAsyncRepository<TEntity, Guid> Repo<TEntity>(IServiceScope scope) where TEntity : class, IEntity<Guid> =>
        scope.ServiceProvider.GetRequiredService<IAsyncRepository<TEntity, Guid>>();

    /// <summary>Empties the scenario tables: a fixture that shares one database across its tests starts each from nothing.</summary>
    private static async Task Reset(Func<IServiceScope> newScope)
    {
        using var scope = newScope();
        await Repo<RelItem>(scope).DeleteManyAsync(x => x.Id != Guid.Empty);
        await Repo<RelMention>(scope).DeleteManyAsync(x => x.Id != Guid.Empty);
        await Repo<RelRun>(scope).DeleteManyAsync(x => x.Id != Guid.Empty);
        await Repo<RelBox>(scope).DeleteManyAsync(x => x.Id != Guid.Empty);
        await Repo<RelArtifact>(scope).DeleteManyAsync(x => x.Id != Guid.Empty);
        await Repo<RelRepo>(scope).DeleteManyAsync(x => x.Id != Guid.Empty);
        await Repo<RelSpace>(scope).DeleteManyAsync(x => x.Id != Guid.Empty);
        await Repo<RelCompany>(scope).DeleteManyAsync(x => x.Id != Guid.Empty);
        await Repo<RelNode>(scope).DeleteManyAsync(x => x.Id != Guid.Empty);
    }

    private static async Task Seed(Func<IServiceScope> newScope, params object[] entities)
    {
        await Reset(newScope);
        using var scope = newScope();
        foreach (var entity in entities)
        {
            var repository = scope.ServiceProvider.GetRequiredService(typeof(IAsyncRepository<,>).MakeGenericType(entity.GetType(), typeof(Guid)));
            await (Task)repository.GetType().GetMethod(nameof(IAsyncRepository<RelItem, Guid>.AddAsync))!.Invoke(repository, [entity, CancellationToken.None])!;
        }

        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync();
    }

    private static async Task<string[]> Titles(Func<IServiceScope> newScope, System.Linq.Expressions.Expression<Func<RelItem, bool>> filter)
    {
        using var scope = newScope();
        return (await Repo<RelItem>(scope).GetFilteredAsync(filter)).Select(x => x.Title).OrderBy(x => x).ToArray();
    }

    /// <summary>The items, mentions and runs of the relation scenarios: A mentions u (live), B mentions u (deleted) and another user, C none.</summary>
    private static async Task<(Guid User, RelItem A, RelItem B, RelItem C)> SeedItemsAsync(Func<IServiceScope> newScope)
    {
        var user = Guid.NewGuid();
        var box = new RelBox { Name = "inbox" };
        var a = new RelItem { Title = "A", BoxId = box.Id };
        var b = new RelItem { Title = "B" };
        var c = new RelItem { Title = "C", BoxId = Guid.NewGuid() };
        await Seed(newScope, box, a, b, c,
            new RelMention { RelItemId = a.Id, UserId = user },
            new RelMention { RelItemId = b.Id, UserId = user, DeletedAt = DateTime.UtcNow },
            new RelMention { RelItemId = b.Id, UserId = Guid.NewGuid() },
            new RelRun { RelItemId = a.Id, Status = "done" },
            new RelRun { RelItemId = a.Id, Status = "failed" });
        return (user, a, b, c);
    }

    /// <summary>Scenario "Any with a condition": exactly the items with a live mention of the user — read and counted.</summary>
    public static async Task AnyWithACondition(Func<IServiceScope> newScope)
    {
        var (user, _, _, _) = await SeedItemsAsync(newScope);

        Assert.That(await Titles(newScope, x => x.Mentions!.Any(m => m.UserId == user && m.DeletedAt == null)), Is.EqualTo(new[] { "A" }));
        Assert.That(await Titles(newScope, x => !x.Mentions!.Any(m => m.UserId == user && m.DeletedAt == null)), Is.EqualTo(new[] { "B", "C" }));
        Assert.That(await Titles(newScope, x => x.Mentions!.Any()), Is.EqualTo(new[] { "A", "B" }));
        Assert.That(await Titles(newScope, x => x.Mentions!.All(m => m.DeletedAt == null)), Is.EqualTo(new[] { "A", "C" }),
            "All holds for an item with no mentions");
        Assert.That(await Titles(newScope, x => x.Title != "C" && (x.Title == "B" || x.Runs!.Any(r => r.Status == "failed"))),
            Is.EqualTo(new[] { "A", "B" }), "a relation inside OR and AND");

        using var scope = newScope();
        Assert.That(await Repo<RelItem>(scope).CountAsync(new QueryOptions<RelItem>().Where(x => x.Mentions!.Any(m => m.UserId == user))),
            Is.EqualTo(2L));
    }

    /// <summary>Scenario "A reference's member in a set-based delete": only workspace w's artifacts go, and the count says so.</summary>
    public static async Task ReferenceMemberInSetBasedDelete(Func<IServiceScope> newScope)
    {
        var (workspace, other) = (Guid.NewGuid(), Guid.NewGuid());
        var space = new RelSpace { WorkspaceId = workspace };
        var first = new RelRepo { WorkspaceId = workspace, SpaceId = space.Id };
        var second = new RelRepo { WorkspaceId = workspace };
        var foreign = new RelRepo { WorkspaceId = other };
        await Seed(newScope, space, first, second, foreign,
            new RelArtifact { RepoId = first.Id, Name = "a1" },
            new RelArtifact { RepoId = second.Id, Name = "a2" },
            new RelArtifact { RepoId = foreign.Id, Name = "a3" },
            new RelArtifact { RepoId = foreign.Id, Name = "a4" });

        using (var read = newScope())
        {
            Assert.That((await Repo<RelArtifact>(read).GetFilteredAsync(x => x.Repo!.Space!.WorkspaceId == workspace)).Select(x => x.Name),
                Is.EqualTo(new[] { "a1" }), "a reference of a reference");
            Assert.That(await Repo<RelArtifact>(read).UpdateManyAsync(x => x.Repo!.WorkspaceId == other, x => new RelArtifact { Downloads = 7 }),
                Is.EqualTo(2L), "a set-based update across the reference");
            var returned = await ((IReturningUpdateRepository<RelArtifact, Guid>)Repo<RelArtifact>(read))
                .UpdateAndGetAsync(x => x.Repo!.WorkspaceId == other && x.Name == "a3", x => new RelArtifact { Downloads = x.Downloads + 1 });
            Assert.That(returned.Select(x => (x.Name, x.Downloads)), Is.EqualTo(new[] { ("a3", 8) }));
        }

        using (var scope = newScope())
        {
            Assert.That(await Repo<RelArtifact>(scope).DeleteManyAsync(x => x.Repo!.WorkspaceId == workspace), Is.EqualTo(2L));
        }

        using var check = newScope();
        Assert.That((await Repo<RelArtifact>(check).GetAllAsync()).Select(x => x.Name).OrderBy(x => x), Is.EqualTo(new[] { "a3", "a4" }));
    }

    /// <summary>Scenario "No related row": the items without runs, and those without a box (unset, or naming none).</summary>
    public static async Task NoRelatedRow(Func<IServiceScope> newScope)
    {
        await SeedItemsAsync(newScope);

        Assert.That(await Titles(newScope, x => !x.Runs!.Any()), Is.EqualTo(new[] { "B", "C" }));
        Assert.That(await Titles(newScope, x => x.Box == null), Is.EqualTo(new[] { "B", "C" }), "C's box id names no box");
        Assert.That(await Titles(newScope, x => x.Box != null), Is.EqualTo(new[] { "A" }));
        Assert.That(await Titles(newScope, x => x.Box!.Name == "inbox"), Is.EqualTo(new[] { "A" }));

        using var scope = newScope();
        Assert.That(await Repo<RelItem>(scope).DeleteManyAsync(x => x.Box == null && !x.Runs!.Any()), Is.EqualTo(2L));
        using var check = newScope();
        Assert.That((await Repo<RelItem>(check).GetAllAsync()).Select(x => x.Title), Is.EqualTo(new[] { "A" }));
    }

    /// <summary>
    ///     Set-based writes whose relation reads the written table itself (a node filtered by its children): one statement
    ///     where the store lets an <c>UPDATE</c>/<c>DELETE</c> read its own table, the matching keys read first and locked
    ///     where it does not (MySQL and MariaDB) — the same rows either way.
    /// </summary>
    public static async Task SelfReferencingRelationInSetBasedWrites(Func<IServiceScope> newScope)
    {
        var root = new RelNode { Name = "root" };
        var a = new RelNode { Name = "a", ParentId = root.Id };
        var b = new RelNode { Name = "b", ParentId = root.Id };
        await Seed(newScope, root, a, b,
            new RelNode { Name = "a1", ParentId = a.Id },
            new RelNode { Name = "b1", ParentId = b.Id });

        using (var scope = newScope())
        {
            var repo = Repo<RelNode>(scope);
            Assert.That(await repo.UpdateManyAsync(x => x.Children!.Any(c => c.Name == "a1"), x => new RelNode { Rank = 10 }), Is.EqualTo(1L));
            Assert.That(await repo.UpdateManyAsync(x => !x.Children!.Any() && x.Rank == 0, x => new RelNode { Rank = 5 }), Is.EqualTo(2L),
                "the leaves");
            var returned = await ((IReturningUpdateRepository<RelNode, Guid>)repo)
                .UpdateAndGetAsync(x => x.Children!.Any(c => c.Rank == 5), x => new RelNode { Rank = x.Rank + 1 });
            Assert.That(returned.Select(x => (x.Name, x.Rank)).OrderBy(x => x.Name), Is.EqualTo(new[] { ("a", 11), ("b", 1) }));
            Assert.That(await repo.DeleteManyAsync(x => !x.Children!.Any() && x.Name.StartsWith("a")), Is.EqualTo(1L), "a1");
            Assert.That(await repo.DeleteManyAsync(x => x.Children!.Any(c => c.Name == "b1")), Is.EqualTo(1L), "b");
        }

        using var check = newScope();
        Assert.That((await Repo<RelNode>(check).GetAllAsync()).Select(x => (x.Name, x.Rank)).OrderBy(x => x.Name),
            Is.EqualTo(new[] { ("a", 11), ("b1", 5), ("root", 0) }));
    }

    /// <summary>The same writes inside an explicit transaction: the keys-first read and the write join it, and a rollback undoes both.</summary>
    public static async Task SelfReferencingRelationInsideATransaction(Func<IServiceScope> newScope)
    {
        var parent = new RelNode { Name = "parent" };
        await Seed(newScope, parent, new RelNode { Name = "child", ParentId = parent.Id });

        using (var scope = newScope())
        {
            var unitOfWork = (Relational.Repository.RelationalUnitOfWork)scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.BeginTransactionAsync();
            Assert.That(await Repo<RelNode>(scope).UpdateManyAsync(x => x.Children!.Any(), x => new RelNode { Rank = 1 }), Is.EqualTo(1L));
            Assert.That(await Repo<RelNode>(scope).DeleteManyAsync(x => !x.Children!.Any()), Is.EqualTo(1L), "the child");
            await unitOfWork.RollbackTransactionAsync();
        }

        using var check = newScope();
        Assert.That((await Repo<RelNode>(check).GetAllAsync()).Select(x => (x.Name, x.Rank)).OrderBy(x => x.Name),
            Is.EqualTo(new[] { ("child", 0), ("parent", 0) }));
    }

    /// <summary>A relation test mixing the related row with the entity's own members is refused with the reason.</summary>
    public static void CorrelationIsRefused(Func<IServiceScope> newScope)
    {
        using var scope = newScope();
        var refusal = Assert.ThrowsAsync<NotSupportedException>(() =>
            Repo<RelItem>(scope).GetFilteredAsync(x => x.Mentions!.Any(m => m.RelItemId == x.Id)));
        Assert.That(refusal!.Message, Does.Contain("entity's own members"));
    }

    /// <summary>Explain names the mechanism: a correlated EXISTS.</summary>
    public static void ExplainNamesTheMechanism(Func<IServiceScope> newScope)
    {
        using var scope = newScope();
        var user = Guid.NewGuid();
        var plan = ((IExplainableRepository<RelItem>)Repo<RelItem>(scope))
            .Explain(new QueryOptions<RelItem>().Where(x => x.Mentions!.Any(m => m.UserId == user) && x.Box == null));
        Assert.That(plan.Statement, Does.Contain("EXISTS (SELECT 1 FROM"));
        Assert.That(plan.Notes, Has.Some.Contains("correlated EXISTS"));
        Assert.That(plan.Notes, Has.Some.Contains("correlated NOT EXISTS"));
    }

    /// <summary>Scenario "Equality and containment", plus prefix, suffix, the folded forms and a literal wildcard.</summary>
    public static async Task TextWithoutRegardToCase(Func<IServiceScope> newScope)
    {
        await Seed(newScope,
            new RelCompany { Name = "Acme Corp", Rank = 1 },
            new RelCompany { Name = "acme_corp", Rank = 2 },
            new RelCompany { Name = "Acme [100%]", Rank = 3 });

        using var scope = newScope();
        var repo = Repo<RelCompany>(scope);
        async Task<string[]> Names(System.Linq.Expressions.Expression<Func<RelCompany, bool>> filter) =>
            (await repo.GetFilteredAsync(filter)).Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        Assert.That(await Names(x => x.Name.Equals("ACME CORP", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(new[] { "Acme Corp" }));
        Assert.That(await Names(x => x.Name.Contains("ME_C", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(new[] { "acme_corp" }),
            "the '_' is taken literally");
        Assert.That(await Names(x => string.Equals(x.Name, "acme CORP", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(new[] { "Acme Corp" }));
        Assert.That(await Names(x => x.Name.StartsWith("ACME", StringComparison.OrdinalIgnoreCase)),
            Is.EqualTo(new[] { "Acme Corp", "Acme [100%]", "acme_corp" }));
        Assert.That(await Names(x => x.Name.EndsWith("CORP", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(new[] { "Acme Corp", "acme_corp" }));
        Assert.That(await Names(x => x.Name.Contains("[100%", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(new[] { "Acme [100%]" }),
            "'[' and '%' are taken literally");
        Assert.That(await Names(x => x.Name.ToLower() == "acme corp"), Is.EqualTo(new[] { "Acme Corp" }));
        Assert.That(await Names(x => x.Name.ToLower() == "ACME CORP"), Is.Empty, "a lower-cased name holds no upper-case letter");
        Assert.That(await Names(x => x.Name.ToUpper().Contains("ME_C")), Is.EqualTo(new[] { "acme_corp" }));
        Assert.That(await Names(x => !x.Name.Contains("corp", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(new[] { "Acme [100%]" }));

        Assert.That(await repo.CountAsync(new QueryOptions<RelCompany>().Where(x => x.Name.StartsWith("acme c", StringComparison.OrdinalIgnoreCase))),
            Is.EqualTo(1L));
        Assert.That(await repo.UpdateManyAsync(x => x.Name.Equals("ACME_CORP", StringComparison.OrdinalIgnoreCase), x => new RelCompany { Rank = 20 }),
            Is.EqualTo(1L));
        Assert.That(await repo.DeleteManyAsync(x => x.Name.EndsWith("%]", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(1L));
        Assert.That((await repo.GetAllAsync()).Select(x => (x.Name, x.Rank)).OrderBy(x => x.Rank),
            Is.EqualTo(new[] { ("Acme Corp", 1), ("acme_corp", 20) }));
    }

    /// <summary><c>Contains</c> over an array (bound to a span by C# 14) and over a list, in reads, counts and set-based writes.</summary>
    public static async Task ArrayAndListContains(Func<IServiceScope> newScope)
    {
        var companies = Enumerable.Range(1, 4).Select(rank => new RelCompany { Name = "c" + rank, Rank = rank }).ToArray();
        await Seed(newScope, companies.Cast<object>().ToArray());

        using var scope = newScope();
        var repo = Repo<RelCompany>(scope);
        var array = new[] { companies[0].Id, companies[1].Id };
        var list = new List<Guid> { companies[1].Id, companies[2].Id };
        var names = new[] { "c3", "c4" };

        Assert.That((await repo.GetFilteredAsync(x => array.Contains(x.Id))).Select(x => x.Rank).Order(), Is.EqualTo(new[] { 1, 2 }));
        Assert.That((await repo.GetFilteredAsync(x => list.Contains(x.Id))).Select(x => x.Rank).Order(), Is.EqualTo(new[] { 2, 3 }));
        Assert.That((await repo.GetFilteredAsync(x => !names.Contains(x.Name))).Select(x => x.Rank).Order(), Is.EqualTo(new[] { 1, 2 }));
        Assert.That(await repo.CountAsync(new QueryOptions<RelCompany>().Where(x => array.Contains(x.Id))), Is.EqualTo(2L));
        Assert.That(await repo.UpdateManyAsync(x => array.Contains(x.Id), x => new RelCompany { Rank = x.Rank + 10 }), Is.EqualTo(2L));
        Assert.That(await repo.UpdateManyAsync(x => list.Contains(x.Id), x => new RelCompany { Name = "listed" }), Is.EqualTo(2L));
        Assert.That(await repo.DeleteManyAsync(x => names.Contains(x.Name)), Is.EqualTo(1L), "c3 was renamed");
        Assert.That((await repo.GetAllAsync()).Select(x => (x.Name, x.Rank)).OrderBy(x => x.Rank),
            Is.EqualTo(new[] { ("listed", 3), ("c1", 11), ("listed", 12) }));
    }
}
