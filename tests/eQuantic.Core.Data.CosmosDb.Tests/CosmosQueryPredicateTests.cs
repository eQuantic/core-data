using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Read;
using eQuantic.Core.Data.Repository.Write;
using eQuantic.Linq.Expressions;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.CosmosDb.Tests;

/// <summary>An item with its mentions and runs (documents of their own collections) and the box it may be in (a reference).</summary>
public sealed class RelItem : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Category { get; set; } = "";

    public string Title { get; set; } = "";

    public string? BoxId { get; set; }

    public RelBox? Box { get; set; }

    public List<RelMention>? Mentions { get; set; }

    public List<RelRun>? Runs { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>A user mentioned in an item, soft-deleted by a date the filter tests.</summary>
public sealed class RelMention : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Category { get; set; } = "";

    public string RelItemId { get; set; } = "";

    public string UserId { get; set; } = "";

    public DateTime? DeletedAt { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>A run of an item.</summary>
public sealed class RelRun : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Category { get; set; } = "";

    public string RelItemId { get; set; } = "";

    public string Status { get; set; } = "";

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>The box an item may be in.</summary>
public sealed class RelBox : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Category { get; set; } = "";

    public string Name { get; set; } = "";

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>An artifact of a repository (a reference whose members the filter reads, and through it the repository's space).</summary>
public sealed class RelArtifact : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Category { get; set; } = "";

    public string RepoId { get; set; } = "";

    public RelRepo? Repo { get; set; }

    public string Name { get; set; } = "";

    public int Downloads { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>A repository of a workspace, in a space.</summary>
public sealed class RelRepo : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Category { get; set; } = "";

    public string WorkspaceId { get; set; } = "";

    public string? SpaceId { get; set; }

    public RelSpace? Space { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>A space of a workspace.</summary>
public sealed class RelSpace : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Category { get; set; } = "";

    public string WorkspaceId { get; set; } = "";

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>A company whose name is matched without regard to case.</summary>
public sealed class RelCompany : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Category { get; set; } = "";

    public string Name { get; set; } = "";

    public int Rank { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>
///     A priority stored as its labels, whose three orders all differ: the members' values (low, medium, high, urgent), the
///     labels' text (a_urgent, high, low, medium) and the declaration order (high, low, a_urgent, medium).
/// </summary>
[Modeling.EnumType]
public enum RelPriority
{
    [Modeling.StoredAs("high")]
    High = 3,

    [Modeling.StoredAs("low")]
    Low = 1,

    [Modeling.StoredAs("a_urgent")]
    Urgent = 4,

    [Modeling.StoredAs("medium")]
    Medium = 2,
}

/// <summary>A ticket: a priority stored as its labels, an escalation that may be unset, and an alias that may hold no text.</summary>
public sealed class RelTicket : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Category { get; set; } = "";

    public string Title { get; set; } = "";

    public RelPriority Priority { get; set; } = RelPriority.Low;

    public RelPriority? Escalation { get; set; }

    public string? Alias { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>
///     The query predicates against the Cosmos DB emulator: a filter across a relation resolves the related keys first (one
///     <c>SELECT DISTINCT VALUE</c> over the related container per test — a relation crosses containers, which Cosmos DB
///     cannot join) and reads with <c>IN</c>/<c>NOT IN</c> over them, in reads, counts and set-based writes (the patch's
///     condition holding the resolved keys); text without regard to case is <c>STRINGEQUALS</c>/<c>CONTAINS</c>/
///     <c>STARTSWITH</c>/<c>ENDSWITH</c> with their case-insensitive flag; and <c>Contains</c> over an array or a list is
///     <c>IN</c>. Each related entity has its own container, so the keys read are its documents' only.
/// </summary>
[TestFixture]
public sealed class CosmosQueryPredicateTests : CosmosIntegrationTest
{
    private static IServiceScope NewScope() => CosmosTestServer.Provider.CreateScope();

    private static IAsyncRepository<TEntity, string> RepoOf<TEntity>(IServiceScope scope) where TEntity : class, IEntity<string> =>
        scope.ServiceProvider.GetRequiredService<IAsyncRepository<TEntity, string>>();

    private async Task Seed(params object[] entities)
    {
        using var scope = NewScope();
        foreach (var entity in entities)
        {
            entity.GetType().GetProperty("Category")!.SetValue(entity, Partition);
            var repository = scope.ServiceProvider.GetRequiredService(typeof(IAsyncRepository<,>).MakeGenericType(entity.GetType(), typeof(string)));
            await (Task)repository.GetType().GetMethod(nameof(IAsyncRepository<RelItem, string>.AddAsync))!.Invoke(repository, [entity, CancellationToken.None])!;
        }

        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync();
    }

    private async Task<string[]> Titles(System.Linq.Expressions.Expression<Func<RelItem, bool>> filter)
    {
        using var scope = NewScope();
        var partition = Partition;
        return (await RepoOf<RelItem>(scope).GetFilteredAsync(filter.AndAlso(x => x.Category == partition)))
            .Select(x => x.Title).OrderBy(x => x).ToArray();
    }

    private async Task<string> SeedItemsAsync()
    {
        var user = Guid.NewGuid().ToString("N");
        var box = new RelBox { Name = "inbox" };
        var a = new RelItem { Title = "A", BoxId = box.Id };
        var b = new RelItem { Title = "B" };
        var c = new RelItem { Title = "C", BoxId = Guid.NewGuid().ToString("N") };
        await Seed(box, a, b, c,
            new RelMention { RelItemId = a.Id, UserId = user },
            new RelMention { RelItemId = b.Id, UserId = user, DeletedAt = DateTime.UtcNow },
            new RelMention { RelItemId = b.Id, UserId = Guid.NewGuid().ToString("N") },
            new RelRun { RelItemId = a.Id, Status = "done" },
            new RelRun { RelItemId = a.Id, Status = "failed" });
        return user;
    }

    [Test]
    public async Task Any_with_a_condition()
    {
        var user = await SeedItemsAsync();
        var partition = Partition;

        Assert.That(await Titles(x => x.Mentions!.Any(m => m.UserId == user && m.DeletedAt == null)), Is.EqualTo(new[] { "A" }));
        Assert.That(await Titles(x => !x.Mentions!.Any(m => m.UserId == user && m.DeletedAt == null)), Is.EqualTo(new[] { "B", "C" }));
        Assert.That(await Titles(x => x.Mentions!.Any(m => m.UserId == user)), Is.EqualTo(new[] { "A", "B" }));
        Assert.That(await Titles(x => x.Mentions!.All(m => m.DeletedAt == null)), Is.EqualTo(new[] { "A", "C" }),
            "All holds for an item with no mentions");
        Assert.That(await Titles(x => x.Title != "C" && (x.Title == "B" || x.Runs!.Any(r => r.Status == "failed"))),
            Is.EqualTo(new[] { "A", "B" }), "a relation inside OR and AND");

        using var scope = NewScope();
        Assert.That(await RepoOf<RelItem>(scope).CountAsync(new QueryOptions<RelItem>()
                .Where(x => x.Category == partition && x.Mentions!.Any(m => m.UserId == user))),
            Is.EqualTo(2L));
        Assert.That(((IRepository<RelItem, string>)RepoOf<RelItem>(scope))
            .GetFiltered(x => x.Category == partition && x.Mentions!.Any(m => m.UserId == user && m.DeletedAt == null))
            .Select(x => x.Title), Is.EqualTo(new[] { "A" }), "the synchronous read resolves the keys too");
    }

    [Test]
    public async Task A_reference_member_in_a_set_based_delete()
    {
        var (workspace, other) = (Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        var partition = Partition;
        var space = new RelSpace { WorkspaceId = workspace };
        var first = new RelRepo { WorkspaceId = workspace, SpaceId = space.Id };
        var second = new RelRepo { WorkspaceId = workspace };
        var foreign = new RelRepo { WorkspaceId = other };
        await Seed(space, first, second, foreign,
            new RelArtifact { RepoId = first.Id, Name = "a1" },
            new RelArtifact { RepoId = second.Id, Name = "a2" },
            new RelArtifact { RepoId = foreign.Id, Name = "a3" },
            new RelArtifact { RepoId = foreign.Id, Name = "a4" });

        using (var read = NewScope())
        {
            Assert.That((await RepoOf<RelArtifact>(read).GetFilteredAsync(x => x.Repo!.Space!.WorkspaceId == workspace)).Select(x => x.Name),
                Is.EqualTo(new[] { "a1" }), "a reference of a reference");
            Assert.That(await RepoOf<RelArtifact>(read).UpdateManyAsync(x => x.Repo!.WorkspaceId == other, x => new RelArtifact { Downloads = 7 }),
                Is.EqualTo(2L), "a set-based update across the reference");
            var returned = await ((IReturningUpdateRepository<RelArtifact, string>)RepoOf<RelArtifact>(read))
                .UpdateAndGetAsync(x => x.Repo!.WorkspaceId == other && x.Name == "a3", x => new RelArtifact { Downloads = x.Downloads + 1 });
            Assert.That(returned.Select(x => (x.Name, x.Downloads)), Is.EqualTo(new[] { ("a3", 8) }));
        }

        using (var scope = NewScope())
        {
            Assert.That(await RepoOf<RelArtifact>(scope).DeleteManyAsync(x => x.Repo!.WorkspaceId == workspace), Is.EqualTo(2L));
        }

        using var check = NewScope();
        Assert.That((await RepoOf<RelArtifact>(check).GetFilteredAsync(x => x.Category == partition)).Select(x => x.Name).OrderBy(x => x),
            Is.EqualTo(new[] { "a3", "a4" }));
    }

    [Test]
    public async Task No_related_row()
    {
        await SeedItemsAsync();
        var partition = Partition;

        Assert.That(await Titles(x => !x.Runs!.Any()), Is.EqualTo(new[] { "B", "C" }));
        Assert.That(await Titles(x => x.Box == null), Is.EqualTo(new[] { "B", "C" }), "C's box id names no box");
        Assert.That(await Titles(x => x.Box != null), Is.EqualTo(new[] { "A" }));
        Assert.That(await Titles(x => x.Box!.Name == "inbox"), Is.EqualTo(new[] { "A" }));
        Assert.That(await Titles(x => !(x.Box != null)), Is.EqualTo(new[] { "B", "C" }),
            "the negation of a related row is no related row, an unset key included, as x.Box == null says");
        Assert.That(await Titles(x => !(x.Box!.Name == "inbox")), Is.EqualTo(new[] { "B", "C" }),
            "no related row meets the test: B's key is unset, C's names no box");

        using var scope = NewScope();
        Assert.That(await RepoOf<RelItem>(scope).DeleteManyAsync(x => x.Category == partition && x.Box == null && !x.Runs!.Any()), Is.EqualTo(2L));
        Assert.That((await RepoOf<RelItem>(scope).GetFilteredAsync(x => x.Category == partition)).Select(x => x.Title), Is.EqualTo(new[] { "A" }));
    }

    [Test]
    public void A_relation_test_reading_the_entity_is_refused()
    {
        using var scope = NewScope();
        var refusal = Assert.ThrowsAsync<NotSupportedException>(() =>
            RepoOf<RelItem>(scope).GetFilteredAsync(x => x.Mentions!.Any(m => m.RelItemId == x.Id)));
        Assert.That(refusal!.Message, Does.Contain("entity's own members"));
    }

    [Test]
    public void Explain_names_the_keys_resolved_first()
    {
        using var scope = NewScope();
        var user = Guid.NewGuid().ToString("N");
        var plan = ((IExplainableRepository<RelItem>)RepoOf<RelItem>(scope))
            .Explain(new QueryOptions<RelItem>().Where(x => x.Mentions!.Any(m => m.UserId == user) && x.Box == null));
        Assert.That(plan.Notes, Has.Some.Contains("resolves first").And.Some.Contains("'rel_mentions'"));
        Assert.That(plan.Notes, Has.Some.Contains("NOT IN"));
    }

    [Test]
    public async Task Text_is_matched_without_regard_to_case()
    {
        await Seed(
            new RelCompany { Name = "Acme Corp", Rank = 1 },
            new RelCompany { Name = "acme_corp", Rank = 2 },
            new RelCompany { Name = "Acme [100%]", Rank = 3 },
            new RelCompany { Name = "Acme.Corp", Rank = 4 });

        using var scope = NewScope();
        var repo = RepoOf<RelCompany>(scope);
        var partition = Partition;
        async Task<string[]> Names(System.Linq.Expressions.Expression<Func<RelCompany, bool>> filter) =>
            (await repo.GetFilteredAsync(filter.AndAlso(x => x.Category == partition)))
            .Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        Assert.That(await Names(x => x.Name.Equals("ACME CORP", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(new[] { "Acme Corp" }));
        Assert.That(await Names(x => x.Name.Contains("ME_C", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(new[] { "acme_corp" }),
            "the '_' is taken literally");
        Assert.That(await Names(x => x.Name.Contains("e.c", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(new[] { "Acme.Corp" }),
            "the '.' is taken literally");
        Assert.That(await Names(x => string.Equals(x.Name, "acme CORP", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(new[] { "Acme Corp" }));
        Assert.That(await Names(x => x.Name.StartsWith("ACME", StringComparison.OrdinalIgnoreCase)),
            Is.EqualTo(new[] { "Acme Corp", "Acme [100%]", "Acme.Corp", "acme_corp" }));
        Assert.That(await Names(x => x.Name.EndsWith("_CORP", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(new[] { "acme_corp" }));
        Assert.That(await Names(x => x.Name.Contains("[100%", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(new[] { "Acme [100%]" }),
            "'[' and '%' are taken literally");
        Assert.That(await Names(x => x.Name.ToLower() == "acme corp"), Is.EqualTo(new[] { "Acme Corp" }));
        Assert.That(await Names(x => x.Name.ToLower() == "ACME CORP"), Is.Empty, "a lower-cased name holds no upper-case letter");
        Assert.That(await Names(x => x.Name.ToUpper().Contains("ME_C")), Is.EqualTo(new[] { "acme_corp" }));
        Assert.That(await Names(x => !x.Name.Contains("corp", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(new[] { "Acme [100%]" }));

        Assert.That(await repo.CountAsync(new QueryOptions<RelCompany>()
                .Where(x => x.Category == partition && x.Name.StartsWith("acme c", StringComparison.OrdinalIgnoreCase))),
            Is.EqualTo(1L));
        Assert.That(await repo.UpdateManyAsync(x => x.Category == partition && x.Name.Equals("ACME_CORP", StringComparison.OrdinalIgnoreCase),
            x => new RelCompany { Rank = 20 }), Is.EqualTo(1L));
        Assert.That(await repo.DeleteManyAsync(x => x.Category == partition && x.Name.EndsWith("%]", StringComparison.OrdinalIgnoreCase)),
            Is.EqualTo(1L));
        Assert.That((await repo.GetFilteredAsync(x => x.Category == partition)).Select(x => (x.Name, x.Rank)).OrderBy(x => x.Rank),
            Is.EqualTo(new[] { ("Acme Corp", 1), ("Acme.Corp", 4), ("acme_corp", 20) }));
    }

    [Test]
    public async Task An_array_and_a_list_are_tested_with_Contains()
    {
        var companies = Enumerable.Range(1, 4).Select(rank => new RelCompany { Name = "c" + rank, Rank = rank }).ToArray();
        await Seed(companies.Cast<object>().ToArray());

        using var scope = NewScope();
        var repo = RepoOf<RelCompany>(scope);
        var partition = Partition;
        var array = new[] { companies[0].Id, companies[1].Id };
        var list = new List<string> { companies[1].Id, companies[2].Id };
        var names = new[] { "c3", "c4" };

        Assert.That((await repo.GetFilteredAsync(x => array.Contains(x.Id))).Select(x => x.Rank).Order(), Is.EqualTo(new[] { 1, 2 }));
        Assert.That((await repo.GetFilteredAsync(x => list.Contains(x.Id))).Select(x => x.Rank).Order(), Is.EqualTo(new[] { 2, 3 }));
        Assert.That((await repo.GetFilteredAsync(x => x.Category == partition && !names.Contains(x.Name))).Select(x => x.Rank).Order(),
            Is.EqualTo(new[] { 1, 2 }));
        Assert.That(await repo.CountAsync(new QueryOptions<RelCompany>().Where(x => array.Contains(x.Id))), Is.EqualTo(2L));
        Assert.That(await repo.UpdateManyAsync(x => array.Contains(x.Id), x => new RelCompany { Rank = x.Rank + 10 }), Is.EqualTo(2L));
        Assert.That(await repo.UpdateManyAsync(x => list.Contains(x.Id), x => new RelCompany { Name = "listed" }), Is.EqualTo(2L));
        Assert.That(await repo.DeleteManyAsync(x => x.Category == partition && names.Contains(x.Name)), Is.EqualTo(1L), "c3 was renamed");
        Assert.That((await repo.GetFilteredAsync(x => x.Category == partition)).Select(x => (x.Name, x.Rank)).OrderBy(x => x.Rank),
            Is.EqualTo(new[] { ("listed", 3), ("c1", 11), ("listed", 12) }));
    }

    private Task SeedTicketsAsync() => Seed(
        new RelTicket { Title = "t1", Priority = RelPriority.Low, Alias = "Alpha" },
        new RelTicket { Title = "t2", Priority = RelPriority.Medium, Escalation = RelPriority.Low },
        new RelTicket { Title = "t3", Priority = RelPriority.High, Escalation = RelPriority.Urgent, Alias = "beta" },
        new RelTicket { Title = "t4", Priority = RelPriority.Urgent, Escalation = RelPriority.Medium, Alias = "ALPHA" });

    private async Task<string[]> TicketTitles(System.Linq.Expressions.Expression<Func<RelTicket, bool>> filter)
    {
        using var scope = NewScope();
        var partition = Partition;
        return (await RepoOf<RelTicket>(scope).GetFilteredAsync(filter.AndAlso(x => x.Category == partition)))
            .Select(x => x.Title).Order().ToArray();
    }

    private async Task<string[]> OrderedTitles(Func<QueryOptions<RelTicket>, QueryOptions<RelTicket>> order)
    {
        using var scope = NewScope();
        var partition = Partition;
        return (await RepoOf<RelTicket>(scope).GetAllAsync(order(new QueryOptions<RelTicket>().Where(x => x.Category == partition))))
            .Select(x => x.Title).ToArray();
    }

    [Test]
    public async Task Labels_compare_and_order_by_their_members_values()
    {
        await SeedTicketsAsync();
        var medium = RelPriority.Medium;

        Assert.That(await TicketTitles(x => x.Priority >= RelPriority.Medium), Is.EqualTo(new[] { "t2", "t3", "t4" }));
        Assert.That(await TicketTitles(x => x.Priority < RelPriority.High), Is.EqualTo(new[] { "t1", "t2" }));
        Assert.That(await TicketTitles(x => medium < x.Priority), Is.EqualTo(new[] { "t3", "t4" }), "a captured value, on the left");
        Assert.That(await TicketTitles(x => x.Priority == RelPriority.Medium), Is.EqualTo(new[] { "t2" }));
        Assert.That(await TicketTitles(x => x.Priority != RelPriority.Medium), Is.EqualTo(new[] { "t1", "t3", "t4" }));
        Assert.That(await TicketTitles(x => x.Priority > RelPriority.Urgent), Is.Empty, "no member is above the highest");
        Assert.That(await TicketTitles(x => x.Escalation >= RelPriority.Medium), Is.EqualTo(new[] { "t3", "t4" }),
            "a nullable member: null is not >= anything");
        Assert.That(await TicketTitles(x => !(x.Escalation >= RelPriority.Medium)), Is.EqualTo(new[] { "t1", "t2" }),
            "under a negation null passes, as in C#");

        // An ORDER BY would order the labels' text: the read runs one query per member, in the order of their values.
        Assert.That(await OrderedTitles(options => options.OrderBy(x => x.Priority)), Is.EqualTo(new[] { "t1", "t2", "t3", "t4" }));
        Assert.That(await OrderedTitles(options => options.OrderByDescending(x => x.Priority)), Is.EqualTo(new[] { "t4", "t3", "t2", "t1" }));
        Assert.That(await OrderedTitles(options => options.OrderByDescending(x => x.Priority).Skip(1).Take(2)), Is.EqualTo(new[] { "t3", "t2" }),
            "the window is carried across the parts");
        Assert.That(await OrderedTitles(options => options.OrderBy(x => x.Escalation)), Is.EqualTo(new[] { "t1", "t2", "t4", "t3" }),
            "a nullable member: its nulls first, as Cosmos DB orders them");
        Assert.That(await OrderedTitles(options => options.OrderBy(x => x.Escalation, NullsOrder.Last)), Is.EqualTo(new[] { "t2", "t4", "t3", "t1" }));

        using var scope = NewScope();
        var repo = RepoOf<RelTicket>(scope);
        var partition = Partition;
        var later = Assert.ThrowsAsync<NotSupportedException>(() =>
            repo.GetAllAsync(new QueryOptions<RelTicket>().Where(x => x.Category == partition).OrderBy(x => x.Title).ThenBy(x => x.Priority)));
        Assert.That(later!.Message, Does.Contain("labels"), "a later sort cannot split the read");
        Assert.That(await repo.CountAsync(new QueryOptions<RelTicket>().Where(x => x.Category == partition && x.Priority >= RelPriority.Medium)),
            Is.EqualTo(3L));
        Assert.That(await repo.UpdateManyAsync(x => x.Category == partition && x.Priority >= RelPriority.High, x => new RelTicket { Alias = "hot" }),
            Is.EqualTo(2L));
        Assert.That(await TicketTitles(x => x.Alias == "hot"), Is.EqualTo(new[] { "t3", "t4" }));
    }

    [Test]
    public async Task A_negated_text_equality_keeps_the_rows_whose_member_is_null()
    {
        await SeedTicketsAsync();

        Assert.That(await TicketTitles(x => string.Equals(x.Alias, "alpha", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(new[] { "t1", "t4" }));
        Assert.That(await TicketTitles(x => !string.Equals(x.Alias, "alpha", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(new[] { "t2", "t3" }));
        Assert.That(await TicketTitles(x => !x.Alias!.Equals("alpha", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(new[] { "t2", "t3" }));
        Assert.That(await TicketTitles(x => !"alpha".Equals(x.Alias, StringComparison.OrdinalIgnoreCase)), Is.EqualTo(new[] { "t2", "t3" }));
        Assert.That(await TicketTitles(x => x.Alias!.ToLower() != "alpha"), Is.EqualTo(new[] { "t2", "t3" }));
        Assert.That(await TicketTitles(x => !(x.Alias!.ToUpper() == "ALPHA")), Is.EqualTo(new[] { "t2", "t3" }));
    }

    [Test]
    public async Task A_set_based_update_matching_text_without_regard_to_case_patches_and_counts_its_matches()
    {
        await Seed(
            new RelCompany { Name = "Acme Corp", Rank = 1 },
            new RelCompany { Name = "ACME Labs", Rank = 2 },
            new RelCompany { Name = "Globex", Rank = 3 });

        using var scope = NewScope();
        var repo = RepoOf<RelCompany>(scope);
        var partition = Partition;
        Assert.That(await repo.UpdateManyAsync(x => x.Category == partition && x.Name.Contains("acme", StringComparison.OrdinalIgnoreCase),
            x => new RelCompany { Rank = 10 }), Is.EqualTo(2L), "the patch's condition would answer 412 and patch nothing");
        Assert.That(await repo.UpdateManyAsync(x => x.Category == partition && x.Name.StartsWith("GLOB", StringComparison.OrdinalIgnoreCase),
            x => new RelCompany { Rank = 30 }), Is.EqualTo(1L));
        Assert.That(await repo.UpdateManyAsync(x => x.Category == partition && x.Name.ToLower() == "acme labs",
            x => new RelCompany { Rank = x.Rank + 1 }), Is.EqualTo(1L));
        Assert.That((await repo.GetFilteredAsync(x => x.Category == partition)).Select(x => (x.Name, x.Rank)).OrderBy(x => x.Name, StringComparer.Ordinal),
            Is.EqualTo(new[] { ("ACME Labs", 11), ("Acme Corp", 10), ("Globex", 30) }));
    }

    [Test]
    public void A_relation_condition_in_a_delegate_is_refused()
    {
        using var scope = NewScope();
        Func<RelMention, bool> live = m => m.DeletedAt == null;
        var refusal = Assert.ThrowsAsync<NotSupportedException>(() => RepoOf<RelItem>(scope).GetFilteredAsync(x => x.Mentions!.Any(live)));
        Assert.That(refusal!.Message, Does.Contain("'Mentions.Any'").And.Contain("Write the lambda inline"));
    }

    [Test]
    public async Task A_minimum_and_a_maximum_of_labels_answer_by_value()
    {
        await SeedTicketsAsync();
        using var scope = NewScope();
        var repo = RepoOf<RelTicket>(scope);
        var aggregates = (IAggregateReadRepository<RelTicket>)repo;
        var partition = Partition;
        QueryOptions<RelTicket> Scoped() => new QueryOptions<RelTicket>().Where(x => x.Category == partition);

        Assert.That(await aggregates.MinAsync(x => x.Priority, Scoped()), Is.EqualTo(RelPriority.Low), "not 'a_urgent', the smallest label");
        Assert.That(await aggregates.MaxAsync(x => x.Priority, Scoped()), Is.EqualTo(RelPriority.Urgent), "not 'medium', the largest label");
        Assert.That(await aggregates.MinAsync(x => x.Escalation, Scoped()), Is.EqualTo(RelPriority.Low), "a nullable member: nulls are skipped");
        Assert.That(await aggregates.MaxAsync(x => x.Escalation, Scoped()), Is.EqualTo(RelPriority.Urgent));
        Assert.That(await aggregates.MaxAsync(x => x.Priority, new QueryOptions<RelTicket>().Where(x => x.Category == partition && x.Priority < RelPriority.High)),
            Is.EqualTo(RelPriority.Medium), "under a filter");
        Assert.That(await aggregates.MaxAsync(x => (int)x.Priority, Scoped()), Is.EqualTo(4), "a converted member answers the member's number");
        Assert.That(await aggregates.MinAsync(x => x.Escalation, new QueryOptions<RelTicket>().Where(x => x.Category == partition && x.Title == "t1")),
            Is.Null, "no value to take");

        Assert.That(Assert.ThrowsAsync<NotSupportedException>(() => repo.SumAsync(x => (int)x.Priority, Scoped()))!.Message, Does.Contain("labels"));
        Assert.That(Assert.ThrowsAsync<NotSupportedException>(() => aggregates.AverageAsync(x => x.Priority, Scoped()))!.Message, Does.Contain("labels"));
    }

    [Test]
    public async Task The_public_query_shaping_reads_the_labels_from_the_client_s_serializer()
    {
        await SeedTicketsAsync();
        var container = Database.GetContainer(CosmosTestServer.ContainerName);

        // An order by a member stored as labels cannot be one ORDER BY: refused, whichever way the enum was declared.
        var fluent = Assert.Throws<NotSupportedException>(() =>
            container.GetItemLinqQueryable<RunRecord>().ApplyQueryOptions(new QueryOptions<RunRecord>().OrderBy(x => x.Channel)));
        Assert.That(fluent!.Message, Does.Contain("Channel").And.Contain("labels"), "declared by Enum<Channel>() on the model");
        var annotated = Assert.Throws<NotSupportedException>(() =>
            container.GetItemLinqQueryable<RelTicket>().ApplyQueryOptions(new QueryOptions<RelTicket>().OrderByDescending(x => x.Escalation)));
        Assert.That(annotated!.Message, Does.Contain("RelPriority").And.Contain("labels"), "annotated with [EnumType]");
        Assert.That(() => container.GetItemLinqQueryable<RunRecord>().ApplyQueryOptions(new QueryOptions<RunRecord>().OrderBy(x => x.Day)),
            Throws.Nothing, "an enum stored as its number orders as one");

        // An ordered comparison compares the members' values.
        var partition = Partition;
        var query = container.GetItemLinqQueryable<RelTicket>()
            .ApplyQueryOptions(new QueryOptions<RelTicket>().Where(x => x.Category == partition && x.Priority >= RelPriority.Medium));
        var titles = new List<string>();
        using (var iterator = query.ToFeedIterator())
        {
            while (iterator.HasMoreResults)
            {
                titles.AddRange((await iterator.ReadNextAsync()).Select(x => x.Title));
            }
        }

        Assert.That(titles.Order(), Is.EqualTo(new[] { "t2", "t3", "t4" }));

        // A query no store translates compares and orders enums as C# does.
        var memory = new[] { new RelTicket { Title = "b", Priority = RelPriority.High }, new RelTicket { Title = "a", Priority = RelPriority.Low } }
            .AsQueryable().ApplyQueryOptions(new QueryOptions<RelTicket>().OrderBy(x => x.Priority));
        Assert.That(memory.Select(x => x.Title), Is.EqualTo(new[] { "a", "b" }));
    }
}
