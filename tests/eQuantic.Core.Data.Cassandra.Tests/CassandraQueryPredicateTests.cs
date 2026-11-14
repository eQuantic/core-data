using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Read;
using eQuantic.Core.Data.Repository.Write;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>An item of a space, with its mentions and runs (rows of their own tables) and the box it may be in.</summary>
public sealed class RelItem : IEntity<Guid>
{
    public Guid Space { get; set; }

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = "";

    public Guid? BoxId { get; set; }

    public RelBox? Box { get; set; }

    public List<RelMention>? Mentions { get; set; }

    public List<RelRun>? Runs { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A user mentioned in an item: the table is read by user.</summary>
public sealed class RelMention : IEntity<Guid>
{
    public Guid UserId { get; set; }

    public Guid RelItemId { get; set; }

    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime? DeletedAt { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A run of an item: the table is read by item.</summary>
public sealed class RelRun : IEntity<Guid>
{
    public Guid RelItemId { get; set; }

    public Guid Id { get; set; } = Guid.NewGuid();

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

/// <summary>An artifact of a repository: the table is read by repository.</summary>
public sealed class RelArtifact : IEntity<Guid>
{
    public Guid RepoId { get; set; }

    public Guid Id { get; set; } = Guid.NewGuid();

    public RelRepo? Repo { get; set; }

    public string Name { get; set; } = "";

    public int Downloads { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A repository of a workspace, in a space: the table is read by workspace.</summary>
public sealed class RelRepo : IEntity<Guid>
{
    public Guid WorkspaceId { get; set; }

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? SpaceId { get; set; }

    public RelSpace? Space { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A space of a workspace: the table is read by workspace.</summary>
public sealed class RelSpace : IEntity<Guid>
{
    public Guid WorkspaceId { get; set; }

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A company of a directory, whose name a case-insensitive search index serves.</summary>
public sealed class RelCompany : IEntity<Guid>
{
    public string Directory { get; set; } = "";

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public string Note { get; set; } = "";

    public int Rank { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>
///     The query predicates against a real Cassandra: a filter across a relation resolves the related keys first (a
///     relation crosses tables, which CQL cannot join; one read per test, under the read's opt-ins) and reads with
///     <c>IN</c> over them — pushed down where the table's key serves it, and the negated forms evaluated client-side
///     behind <c>.AllowClientEvaluation()</c>; a set-based write whose resolved keys address the table's partition runs as
///     any keyed write. Text without regard to case is a <c>LIKE</c> on a case-insensitive SASI index where the member
///     declares one, and the client-side residual otherwise. Each table is modelled for the reads the scenarios make.
/// </summary>
[TestFixture]
public sealed class CassandraQueryPredicateTests : CassandraIntegrationTest
{
    private CassandraTestDatabase _db = null!;

    private static void Model(CassandraModelBuilder builder) => builder
        .Entity<RelItem>(entity => entity.PartitionKey(x => x.Space).ClusteringKey(x => x.Id).Key(x => x.Id))
        .Entity<RelMention>(entity => entity.PartitionKey(x => x.UserId).ClusteringKey(x => x.RelItemId).ClusteringKey(x => x.Id).Key(x => x.Id))
        .Entity<RelRun>(entity => entity.PartitionKey(x => x.RelItemId).ClusteringKey(x => x.Id).Key(x => x.Id))
        .Entity<RelBox>(entity => entity.PartitionKey(x => x.Id))
        .Entity<RelArtifact>(entity => entity.PartitionKey(x => x.RepoId).ClusteringKey(x => x.Id).Key(x => x.Id))
        .Entity<RelRepo>(entity => entity.PartitionKey(x => x.WorkspaceId).ClusteringKey(x => x.Id).Key(x => x.Id))
        .Entity<RelSpace>(entity => entity.PartitionKey(x => x.WorkspaceId).ClusteringKey(x => x.Id).Key(x => x.Id))
        .Entity<RelCompany>(entity => entity
            .PartitionKey(x => x.Directory)
            .ClusteringKey(x => x.Id)
            .Key(x => x.Id)
            .SearchIndex(x => x.Name, CassandraSearchMode.Contains, caseInsensitive: true));

    /// <remarks>
    ///     These scenarios write the same rows with plain statements (the seed, a keyed update or delete) and with lightweight
    ///     transactions (a conditional write), one right after the other. Cassandra orders writes by their timestamps: a plain
    ///     write carries the client's (the driver's default), a lightweight transaction the coordinator's — and this host and
    ///     the container's VM keep different clocks, by more than the milliseconds between two steps, so a later write could
    ///     lose to an earlier one. The fixture's session lets the server stamp every write (a timestamp generator answering
    ///     <see cref="long.MinValue" />), so one clock orders them, as synchronized clocks do in a cluster.
    /// </remarks>
    [SetUp]
    public async Task CreateKeyspace()
    {
        var keyspace = "ks_" + Guid.NewGuid().ToString("N");
        _db = new CassandraTestDatabase(CassandraTestServer.Host, CassandraTestServer.Port, keyspace,
            Model, [typeof(SchemaSetupMigration).Assembly], services => services.Replace(ServiceDescriptor.Singleton<global::Cassandra.ISession>(_ =>
            {
                var cluster = global::Cassandra.Cluster.Builder()
                    .AddContactPoints(CassandraTestServer.Host)
                    .WithPort(CassandraTestServer.Port)
                    .WithTimestampGenerator(new ServerTimestamps())
                    .Build();
                var session = cluster.Connect();
                session.Execute($"CREATE KEYSPACE IF NOT EXISTS {keyspace} WITH replication = {{'class':'SimpleStrategy','replication_factor':1}}");
                session.ChangeKeyspace(keyspace);
                return session;
            })));
        await _db.Resolve<IMigrationExecutor>().ApplyAsync(new MigrationBuilder()
            .For<RelItem>(table => table.EnsureCollection())
            .For<RelMention>(table => table.EnsureCollection())
            .For<RelRun>(table => table.EnsureCollection())
            .For<RelBox>(table => table.EnsureCollection())
            .For<RelArtifact>(table => table.EnsureCollection())
            .For<RelRepo>(table => table.EnsureCollection())
            .For<RelSpace>(table => table.EnsureCollection())
            .For<RelCompany>(table => table.EnsureCollection())
            .Operations);
    }

    [TearDown]
    public void DropKeyspace() => _db?.Dispose();

    /// <summary>Lets the server stamp each write: the driver sends no timestamp when the generator answers <see cref="long.MinValue" />.</summary>
    private sealed class ServerTimestamps : global::Cassandra.ITimestampGenerator
    {
        public long Next() => long.MinValue;
    }

    private IServiceScope NewScope() => _db.NewScope();

    private static IAsyncRepository<TEntity, Guid> Repo<TEntity>(IServiceScope scope) where TEntity : class, IEntity<Guid> =>
        scope.ServiceProvider.GetRequiredService<IAsyncRepository<TEntity, Guid>>();

    private async Task Seed(params object[] entities)
    {
        using var scope = NewScope();
        foreach (var entity in entities)
        {
            var repository = scope.ServiceProvider.GetRequiredService(typeof(IAsyncRepository<,>).MakeGenericType(entity.GetType(), typeof(Guid)));
            await (Task)repository.GetType().GetMethod(nameof(IAsyncRepository<RelItem, Guid>.AddAsync))!.Invoke(repository, [entity, CancellationToken.None])!;
        }

        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync();
    }

    /// <summary>The items of a space a filter keeps; the negated relation tests run client-side, behind the opt-in.</summary>
    private async Task<string[]> Titles(Guid space, System.Linq.Expressions.Expression<Func<RelItem, bool>> filter)
    {
        using var scope = NewScope();
        var options = new QueryOptions<RelItem>().Where(x => x.Space == space).AllowFiltering().AllowClientEvaluation();
        return (await Repo<RelItem>(scope).GetFilteredAsync(filter, options)).Select(x => x.Title).OrderBy(x => x).ToArray();
    }

    private async Task<(Guid Space, Guid User)> SeedItemsAsync()
    {
        var (space, user) = (Guid.NewGuid(), Guid.NewGuid());
        var box = new RelBox { Name = "inbox" };
        var a = new RelItem { Space = space, Title = "A", BoxId = box.Id };
        var b = new RelItem { Space = space, Title = "B" };
        var c = new RelItem { Space = space, Title = "C", BoxId = Guid.NewGuid() };
        await Seed(box, a, b, c,
            new RelMention { RelItemId = a.Id, UserId = user },
            new RelMention { RelItemId = b.Id, UserId = user, DeletedAt = DateTime.UtcNow },
            new RelMention { RelItemId = b.Id, UserId = Guid.NewGuid() },
            new RelRun { RelItemId = a.Id, Status = "done" },
            new RelRun { RelItemId = a.Id, Status = "failed" });
        return (space, user);
    }

    [Test]
    public async Task Any_with_a_condition()
    {
        var (space, user) = await SeedItemsAsync();

        Assert.That(await Titles(space, x => x.Mentions!.Any(m => m.UserId == user && m.DeletedAt == null)), Is.EqualTo(new[] { "A" }));
        Assert.That(await Titles(space, x => !x.Mentions!.Any(m => m.UserId == user && m.DeletedAt == null)), Is.EqualTo(new[] { "B", "C" }));
        Assert.That(await Titles(space, x => x.Mentions!.Any()), Is.EqualTo(new[] { "A", "B" }));
        Assert.That(await Titles(space, x => x.Mentions!.All(m => m.DeletedAt == null)), Is.EqualTo(new[] { "A", "C" }),
            "All holds for an item with no mentions");
        Assert.That(await Titles(space, x => x.Title != "C" && (x.Title == "B" || x.Runs!.Any(r => r.Status == "failed"))),
            Is.EqualTo(new[] { "A", "B" }), "a relation inside OR and AND");

        using var scope = NewScope();
        Assert.That(await Repo<RelItem>(scope).CountAsync(new QueryOptions<RelItem>()
                .Where(x => x.Space == space && x.Mentions!.Any(m => m.UserId == user))),
            Is.EqualTo(2L), "resolved to IN on the clustering key within the space: native, no opt-in");
    }

    [Test]
    public async Task A_relation_read_keeps_the_gates()
    {
        var (space, user) = await SeedItemsAsync();
        using var scope = NewScope();

        var refusal = Assert.ThrowsAsync<NotSupportedException>(() => Repo<RelItem>(scope).GetFilteredAsync(
            x => x.Mentions!.Any(m => m.UserId == user && m.DeletedAt == null), new QueryOptions<RelItem>().Where(x => x.Space == space)));
        Assert.That(refusal!.Message, Does.Contain("AllowClientEvaluation"),
            "the related read's null test runs client-side, which the read did not opt into");
    }

    [Test]
    public async Task A_reference_member_in_a_set_based_delete()
    {
        var (workspace, other) = (Guid.NewGuid(), Guid.NewGuid());
        var space = new RelSpace { WorkspaceId = workspace };
        var first = new RelRepo { WorkspaceId = workspace, SpaceId = space.Id };
        var second = new RelRepo { WorkspaceId = workspace };
        var foreign = new RelRepo { WorkspaceId = other };
        var artifacts = new[]
        {
            new RelArtifact { RepoId = first.Id, Name = "a1" },
            new RelArtifact { RepoId = second.Id, Name = "a2" },
            new RelArtifact { RepoId = foreign.Id, Name = "a3" },
            new RelArtifact { RepoId = foreign.Id, Name = "a4" },
        };
        await Seed([space, first, second, foreign, .. artifacts]);

        using (var read = NewScope())
        {
            Assert.That((await Repo<RelArtifact>(read).GetFilteredAsync(x => x.Repo!.Space!.WorkspaceId == workspace,
                    new QueryOptions<RelArtifact>().AllowFiltering().AllowClientEvaluation())).Select(x => x.Name),
                Is.EqualTo(new[] { "a1" }),
                "a reference of a reference (the repository's space id is not its key: read whole and kept client-side, opted into)");
            Assert.That(await Repo<RelArtifact>(read).UpdateManyAsync(x => x.Repo!.WorkspaceId == other, x => new RelArtifact { Downloads = 7 }),
                Is.EqualTo(2L), "a set-based update across the reference: the keys address the artifacts' partitions");
            var returned = await ((IReturningUpdateRepository<RelArtifact, Guid>)Repo<RelArtifact>(read))
                .UpdateAndGetAsync(x => x.Repo!.WorkspaceId == other && x.Name == "a3", x => new RelArtifact { Downloads = x.Downloads + 1 });
            Assert.That(returned.Select(x => (x.Name, x.Downloads)), Is.EqualTo(new[] { ("a3", 8) }));

            // Workspace w's two repositories are two partitions of the artifacts: the rows are read across both.
            Assert.That(await Repo<RelArtifact>(read).UpdateManyAsync(x => x.Repo!.WorkspaceId == workspace, x => new RelArtifact { Downloads = 5 }),
                Is.EqualTo(2L));
            var acrossPartitions = await ((IReturningUpdateRepository<RelArtifact, Guid>)Repo<RelArtifact>(read))
                .UpdateAndGetAsync(x => x.Repo!.WorkspaceId == workspace && x.Name == "a2", x => new RelArtifact { Downloads = x.Downloads + 1 });
            Assert.That(acrossPartitions.Select(x => (x.Name, x.Downloads)), Is.EqualTo(new[] { ("a2", 6) }));
        }

        using (var scope = NewScope())
        {
            Assert.That(await Repo<RelArtifact>(scope).DeleteManyAsync(x => x.Repo!.WorkspaceId == workspace), Is.EqualTo(2L));
        }

        using var check = NewScope();
        Assert.That((await Repo<RelArtifact>(check).GetAllAsync()).Select(x => (x.Name, x.Downloads)).OrderBy(x => x.Name),
            Is.EqualTo(new[] { ("a3", 8), ("a4", 7) }));
    }

    [Test]
    public async Task No_related_row()
    {
        var (space, _) = await SeedItemsAsync();

        Assert.That(await Titles(space, x => !x.Runs!.Any()), Is.EqualTo(new[] { "B", "C" }));
        Assert.That(await Titles(space, x => x.Box == null), Is.EqualTo(new[] { "B", "C" }), "C's box id names no box");
        Assert.That(await Titles(space, x => x.Box != null), Is.EqualTo(new[] { "A" }));
        Assert.That(await Titles(space, x => x.Box!.Name == "inbox"), Is.EqualTo(new[] { "A" }));
    }

    [Test]
    public async Task A_negated_relation_in_set_based_writes()
    {
        var (space, _) = await SeedItemsAsync();
        using (var scope = NewScope())
        {
            var repo = Repo<RelItem>(scope);

            // The rows the space finds, less the ones whose key the related read returns: each written by its full key.
            Assert.That(await repo.UpdateManyAsync(x => x.Space == space && !x.Runs!.Any(), x => new RelItem { Title = "idle" }),
                Is.EqualTo(2L), "B and C");
            var boxless = await ((IReturningUpdateRepository<RelItem, Guid>)repo)
                .UpdateAndGetAsync(x => x.Space == space && x.Box == null && x.Title == "idle", x => new RelItem { Title = "boxless" });
            Assert.That(boxless.Select(x => (x.Title, x.BoxId is null)).OrderBy(x => x.Item2),
                Is.EqualTo(new[] { ("boxless", false), ("boxless", true) }),
                "B (no box id) and C (a box id naming no box); the transaction holds the box id at the value read");
            Assert.That(await repo.DeleteManyAsync(x => x.Space == space && !x.Runs!.Any() && x.BoxId == null), Is.EqualTo(1L), "B");
            Assert.That(await repo.DeleteManyAsync(x => x.Space == space && x.Box == null), Is.EqualTo(1L), "C, B being gone");
            Assert.That(await repo.DeleteManyAsync(x => x.Space == space && x.Box == null), Is.EqualTo(0L));
        }

        Assert.That(await Titles(space, _ => true), Is.EqualTo(new[] { "A" }));
    }

    /// <remarks>
    ///     A relation test with no condition (<c>Any()</c>, <c>!Any()</c>, <c>== null</c>) stands for every row of the related
    ///     table: its keys are a read of the whole table, which a read makes only behind <c>.AllowFiltering()</c>, as any scan.
    /// </remarks>
    [Test]
    public async Task A_relation_test_without_a_condition_reads_the_whole_related_table_only_behind_the_reads_opt_in()
    {
        var (space, _) = await SeedItemsAsync();
        using var executed = new ExecutedCql();
        using var scope = NewScope();
        var repo = Repo<RelItem>(scope);

        var refusal = Assert.ThrowsAsync<NotSupportedException>(() => repo.GetFilteredAsync(x => x.Space == space && x.Runs!.Any()));
        Assert.That(refusal!.Message, Does.Contain("AllowFiltering"));
        var missing = Assert.ThrowsAsync<NotSupportedException>(() => repo.GetFilteredAsync(x => x.Space == space && x.Box != null,
            new QueryOptions<RelItem>().AllowClientEvaluation()));
        Assert.That(missing!.Message, Does.Contain("AllowFiltering"));
        Assert.That(executed.On("relrun").Concat(executed.On("relbox")), Is.Empty, "nothing was read before the gate");

        Assert.That((await repo.GetFilteredAsync(x => x.Space == space && x.Runs!.Any(), new QueryOptions<RelItem>().AllowFiltering()))
            .Select(x => x.Title), Is.EqualTo(new[] { "A" }), "opted into the scan, the read runs");
    }

    /// <remarks>
    ///     A write carries no opt-ins, so the related rows a relation test reads have to be partition-scoped: by the
    ///     relation's own condition, or — when the write names its partition — by the keys of the rows the write finds, read
    ///     by the related table's partition key.
    /// </remarks>
    [Test]
    public async Task A_write_reads_a_relation_partition_scoped_or_refuses()
    {
        var (space, user) = await SeedItemsAsync();
        using var executed = new ExecutedCql();
        using var scope = NewScope();
        var repo = Repo<RelItem>(scope);

        var unscoped = Assert.ThrowsAsync<NotSupportedException>(() =>
            repo.UpdateManyAsync(x => x.Runs!.Any(), x => new RelItem { Title = "busy" }));
        Assert.That(unscoped!.Message, Does.Contain("partition key"));
        var byUser = Assert.ThrowsAsync<NotSupportedException>(() =>
            repo.UpdateManyAsync(x => x.Space == space && x.Mentions!.Any(), x => new RelItem { Title = "mentioned" }));
        Assert.That(byUser!.Message, Does.Contain("partition key"), "the mentions are partitioned by user, not by item");
        Assert.That(executed.On("relrun").Concat(executed.On("relmention")).Where(statement => statement.IsRead),
            Has.None.Matches<ExecutedStatement>(statement => !statement.Cql.Contains(" WHERE ", StringComparison.OrdinalIgnoreCase)),
            "no related table was read whole");

        Assert.That(await repo.UpdateManyAsync(x => x.Space == space && x.Runs!.Any(), x => new RelItem { Title = "busy" }), Is.EqualTo(1L), "A");
        Assert.That(executed.On("relrun").Where(statement => statement.IsRead).Select(statement => statement.Cql),
            Has.Some.Contains("RelItemId IN").IgnoreCase.And.All.Contains(" WHERE ").IgnoreCase,
            "the runs were read for the items the write found, by their partition key");
        Assert.That(await repo.UpdateManyAsync(x => x.Space == space && x.Mentions!.Any(m => m.UserId == user), x => new RelItem { Title = "seen" }),
            Is.EqualTo(2L), "a condition on the related partition needs nothing more");
    }

    /// <remarks>
    ///     A user's 250 mentions resolve to an <c>IN</c> of 250 items; the read runs it in chunks, each a query of its own,
    ///     instead of one statement binding every key.
    /// </remarks>
    [Test]
    public async Task An_in_list_a_relation_resolves_to_runs_in_chunks()
    {
        const int count = 250;
        var (space, user) = (Guid.NewGuid(), Guid.NewGuid());
        var items = Enumerable.Range(0, count).Select(index => new RelItem { Space = space, Title = $"item-{index:D3}" }).ToList();
        await Seed([.. items, .. items.Select(item => new RelMention { RelItemId = item.Id, UserId = user })]);
        using var executed = new ExecutedCql();
        using var scope = NewScope();

        var found = (await Repo<RelItem>(scope).GetFilteredAsync(x => x.Space == space && x.Mentions!.Any(m => m.UserId == user))).ToList();

        Assert.That(found.Select(x => x.Id), Is.EquivalentTo(items.Select(x => x.Id)));
        var reads = executed.On("relitem").Where(statement => statement.IsRead).ToList();
        Assert.That(reads, Has.Count.EqualTo(3), "250 keys in chunks of 100");
        Assert.That(reads.Select(statement => statement.Markers), Has.All.LessThanOrEqualTo(101), "the space and at most 100 keys each");
    }

    [Test]
    public void A_negated_relation_in_a_write_without_the_partition_key_is_refused()
    {
        using var scope = NewScope();
        var refusal = Assert.ThrowsAsync<NotSupportedException>(() =>
            Repo<RelItem>(scope).DeleteManyAsync(x => !x.Runs!.Any()));
        Assert.That(refusal!.Message, Does.Contain("partition key"));
    }

    [Test]
    public void A_relation_test_reading_the_entity_is_refused()
    {
        using var scope = NewScope();
        var refusal = Assert.ThrowsAsync<NotSupportedException>(() =>
            Repo<RelItem>(scope).GetFilteredAsync(x => x.Mentions!.Any(m => m.RelItemId == x.Id)));
        Assert.That(refusal!.Message, Does.Contain("entity's own members"));
    }

    [Test]
    public void Explain_names_the_keys_resolved_first()
    {
        using var scope = NewScope();
        var user = Guid.NewGuid();
        var plan = ((IExplainableRepository<RelItem>)Repo<RelItem>(scope))
            .Explain(new QueryOptions<RelItem>().Where(x => x.Mentions!.Any(m => m.UserId == user) && x.Box == null));
        Assert.That(plan.Notes, Has.Some.Contains("resolves first").And.Some.Contains("'relmention'").IgnoreCase);
        Assert.That(plan.Notes, Has.Some.Contains("NOT IN"));
    }

    [Test]
    public async Task Text_is_matched_without_regard_to_case_on_a_case_insensitive_index()
    {
        const string directory = "d";
        await Seed(
            new RelCompany { Directory = directory, Name = "Acme Corp", Note = "Acme Corp", Rank = 1 },
            new RelCompany { Directory = directory, Name = "acme_corp", Note = "acme_corp", Rank = 2 },
            new RelCompany { Directory = directory, Name = "Acme [100%]", Note = "Acme [100%]", Rank = 3 },
            new RelCompany { Directory = directory, Name = "Acme.Corp", Note = "Acme.Corp", Rank = 4 });

        using var scope = NewScope();
        var repo = Repo<RelCompany>(scope);
        async Task<string[]> Names(System.Linq.Expressions.Expression<Func<RelCompany, bool>> filter, QueryOptions<RelCompany>? options = null) =>
            (await repo.GetFilteredAsync(filter, options)).Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        // On the indexed member: a native LIKE, no opt-in.
        Assert.That(await Names(x => x.Name.Equals("ACME CORP", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(new[] { "Acme Corp" }));
        Assert.That(await Names(x => x.Name.Contains("ME_C", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(new[] { "acme_corp" }),
            "SASI's LIKE takes the '_' literally");
        Assert.That(await Names(x => string.Equals(x.Name, "acme CORP", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(new[] { "Acme Corp" }));
        Assert.That(await Names(x => x.Name.StartsWith("ACME", StringComparison.OrdinalIgnoreCase)),
            Is.EqualTo(new[] { "Acme Corp", "Acme [100%]", "Acme.Corp", "acme_corp" }));
        Assert.That(await Names(x => x.Name.EndsWith("_CORP", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(new[] { "acme_corp" }));
        Assert.That(await Names(x => x.Name.ToLower() == "acme corp"), Is.EqualTo(new[] { "Acme Corp" }));
        Assert.That(await Names(x => x.Name.ToUpper().Contains("ME_C")), Is.EqualTo(new[] { "acme_corp" }));
        Assert.That(await repo.CountAsync(new QueryOptions<RelCompany>().Where(x => x.Name.StartsWith("acme c", StringComparison.OrdinalIgnoreCase))),
            Is.EqualTo(1L));

        // What the index cannot serve runs client-side, behind the opt-in, and means the same.
        var clientSide = new QueryOptions<RelCompany>().Where(x => x.Directory == directory).AllowClientEvaluation();
        Assert.That(await Names(x => x.Name.Contains("[100%", StringComparison.OrdinalIgnoreCase), clientSide), Is.EqualTo(new[] { "Acme [100%]" }),
            "a literal '%' cannot push down (SASI's LIKE has no escape)");
        Assert.That(await Names(x => x.Name.ToLower() == "ACME CORP", clientSide), Is.Empty, "a lower-cased name holds no upper-case letter");
        Assert.That(await Names(x => x.Note.Contains("ME_C", StringComparison.OrdinalIgnoreCase), clientSide), Is.EqualTo(new[] { "acme_corp" }),
            "a member without a case-insensitive index");
        Assert.That(await Names(x => !x.Name.Contains("corp", StringComparison.OrdinalIgnoreCase), clientSide), Is.EqualTo(new[] { "Acme [100%]" }));
        var refusal = Assert.ThrowsAsync<NotSupportedException>(() => repo.GetFilteredAsync(x => x.Note.Equals("x", StringComparison.OrdinalIgnoreCase),
            new QueryOptions<RelCompany>().Where(x => x.Directory == directory)));
        Assert.That(refusal!.Message, Does.Contain("AllowClientEvaluation"), "without the opt-in, the residual is refused");
    }

    [Test]
    public async Task An_array_and_a_list_are_tested_with_Contains()
    {
        const string directory = "d";
        var companies = Enumerable.Range(1, 4).Select(rank => new RelCompany { Directory = directory, Name = "c" + rank, Rank = rank }).ToArray();
        await Seed(companies.Cast<object>().ToArray());

        using var scope = NewScope();
        var repo = Repo<RelCompany>(scope);
        var array = new[] { companies[0].Id, companies[1].Id };
        var list = new List<Guid> { companies[1].Id, companies[2].Id };
        var directories = new[] { directory, "elsewhere" };
        var names = new[] { "c3", "c4" };

        Assert.That((await repo.GetFilteredAsync(x => x.Directory == directory && array.Contains(x.Id))).Select(x => x.Rank).Order(),
            Is.EqualTo(new[] { 1, 2 }));
        Assert.That((await repo.GetFilteredAsync(x => x.Directory == directory && list.Contains(x.Id))).Select(x => x.Rank).Order(),
            Is.EqualTo(new[] { 2, 3 }));
        Assert.That((await repo.GetFilteredAsync(x => directories.Contains(x.Directory))).Count(), Is.EqualTo(4), "an array over the partition key");
        Assert.That((await repo.GetFilteredAsync(x => !names.Contains(x.Name),
                new QueryOptions<RelCompany>().Where(x => x.Directory == directory).AllowClientEvaluation())).Select(x => x.Rank).Order(),
            Is.EqualTo(new[] { 1, 2 }), "a negated array runs client-side, the normalized predicate compiled");
        Assert.That(await repo.CountAsync(new QueryOptions<RelCompany>().Where(x => x.Directory == directory && array.Contains(x.Id))), Is.EqualTo(2L));
        Assert.That(await repo.UpdateManyAsync(x => x.Directory == directory && array.Contains(x.Id), x => new RelCompany { Note = "array" }),
            Is.EqualTo(2L));
        Assert.That(await repo.UpdateManyAsync(x => x.Directory == directory && list.Contains(x.Id), x => new RelCompany { Name = "listed" }),
            Is.EqualTo(2L));
        Assert.That(await repo.DeleteManyAsync(x => directories.Contains(x.Directory) && array.Contains(x.Id)), Is.EqualTo(2L));
        Assert.That((await repo.GetAllAsync()).Select(x => (x.Name, x.Rank)).OrderBy(x => x.Rank),
            Is.EqualTo(new[] { ("listed", 3), ("c4", 4) }));
    }
}
