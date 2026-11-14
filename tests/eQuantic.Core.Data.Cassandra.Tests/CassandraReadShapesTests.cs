using System.Collections.Concurrent;
using eQuantic.Core.Data.Cassandra.Repository;
using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Read;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>
///     The read shapes against a real Cassandra: includes read from the related tables with a second <c>IN</c> query
///     (a per-parent limit as <c>PER PARTITION LIMIT</c> where the clustering order serves it), relation counts as
///     <c>COUNT(*) … GROUP BY</c> on the partition key, nulls placed by a client-side sort behind its opt-in (a
///     clustering key is never null), a page without its total, and distinct values as a <c>GROUP BY</c> over a
///     primary-key prefix. What runs client-side does so behind the read's own opt-ins, as every Cassandra read does.
/// </summary>
[TestFixture]
public sealed class CassandraReadShapesTests : CassandraIntegrationTest
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly ConcurrentQueue<string> _statements = new();
    private CassandraTestDatabase _db = null!;

    [SetUp]
    public async Task OpenKeyspace()
    {
        _statements.Clear();
        _db = new CassandraTestDatabase(CassandraTestServer.Host, CassandraTestServer.Port, "ks_" + Guid.NewGuid().ToString("N"),
            model =>
            {
                TestSchema.Configure(model);
                ReadShapesModel.Configure(model);
            },
            [typeof(SchemaSetupMigration).Assembly],
            services => services.AddSingleton<ILoggerFactory>(_ =>
            {
                var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information));
                factory.AddProvider(new StatementLog(_statements));
                return factory;
            }));
        await _db.Resolve<IMigrationRunner>().RunAsync();
        await _db.Resolve<IMigrationExecutor>().ApplyAsync(ReadShapesModel.Tables);
        _statements.Clear();
    }

    [TearDown]
    public void CloseKeyspace() => (_db as IDisposable)?.Dispose();

    private IAsyncRepository<TEntity, Guid> Repo<TEntity>() where TEntity : class, IEntity<Guid> =>
        _db.Resolve<IAsyncRepository<TEntity, Guid>>();

    private Task Commit() => _db.Resolve<CassandraDefaultUnitOfWork>().CommitAsync();

    private async Task Add<TEntity>(params TEntity[] entities) where TEntity : class, IEntity<Guid>
    {
        foreach (var entity in entities)
        {
            await Repo<TEntity>().AddAsync(entity);
        }
    }

    [Test]
    public async Task An_include_orders_text_as_the_cluster_keeps_it()
    {
        var run = new ShapeRun { Name = "tagged" };
        await Add(run);
        await Add(new ShapeTag { ShapeRunId = run.Id, Name = "\U0001F600" }, new ShapeTag { ShapeRunId = run.Id, Name = "\uFF21" },
            new ShapeTag { ShapeRunId = run.Id, Name = "z" });
        await Commit();

        var stored = (await Repo<ShapeTag>().GetAllAsync(new QueryOptions<ShapeTag>().Where(x => x.ShapeRunId == run.Id).OrderBy(x => x.Name)))
            .Select(x => x.Name).ToArray();
        Assert.That(stored, Is.EqualTo(new[] { "z", "\uFF21", "\U0001F600" }), "the cluster keeps text in UTF-8's byte order");

        QueryOptions<ShapeRun> Tagged(Func<IncludeQuery<ShapeTag>, IncludeQuery<ShapeTag>> shape) =>
            new QueryOptions<ShapeRun>().Where(x => x.Id == run.Id).Include(x => x.Tags, shape);
        var ordered = (await Repo<ShapeRun>().GetAllAsync(Tagged(tags => tags.OrderBy(t => t.Name)))).Single();
        Assert.That(ordered.Tags!.Select(t => t.Name), Is.EqualTo(stored), "ordered client-side, as the cluster orders it");

        _statements.Clear();
        var limited = (await Repo<ShapeRun>().GetAllAsync(Tagged(tags => tags.OrderBy(t => t.Name).Take(2)))).Single();
        Assert.That(_statements, Has.Some.Contains("PER PARTITION LIMIT 2"));
        Assert.That(limited.Tags!.Select(t => t.Name), Is.EqualTo(stored.Take(2)), "the cluster's first two, in its order");
    }

    [Test]
    public async Task An_include_is_filtered_ordered_and_limited_per_parent()
    {
        var run = new ShapeRun { Name = "build" };
        var other = new ShapeRun { Name = "deploy" };
        await Add(run, other);
        await Add(
            Step(run, "success", 1), Step(run, "success", 2), Step(run, "skipped", 3), Step(run, "failure", 4), Step(run, "skipped", 5),
            Step(run, "success", 6), Step(other, "success", 1), Step(other, "skipped", 2), Step(other, "success", 3));
        await Commit();

        var ids = new List<Guid> { run.Id, other.Id };
        QueryOptions<ShapeRun> Shaped(Func<IncludeQuery<ShapeStep>, IncludeQuery<ShapeStep>> shape) =>
            new QueryOptions<ShapeRun>().Where(x => ids.Contains(x.Id)).Include(x => x.Steps, shape);

        Assert.That(async () => await Repo<ShapeRun>().GetAllAsync(Shaped(steps => steps.Where(s => s.Status != "skipped"))),
            Throws.InstanceOf<NotSupportedException>().With.Message.Contains("AllowClientEvaluation"),
            "CQL has no '!=': the include's filter runs client-side only behind the read's opt-in");

        var runs = (await Repo<ShapeRun>().GetAllAsync(Shaped(steps => steps
                .Where(s => s.Status != "skipped").OrderByDescending(s => s.StartedAt).Take(3)).AllowClientEvaluation()))
            .ToDictionary(x => x.Id);
        Assert.That(runs[run.Id].Steps!.Select(s => s.StartedAt), Is.EqualTo(new[] { At(6), At(4), At(2) }),
            "the three newest steps that were not skipped, newest first");
        Assert.That(runs[other.Id].Steps!.Select(s => s.StartedAt), Is.EqualTo(new[] { At(3), At(1) }));
        Assert.That(_statements, Has.Some.Contains("WHERE ShapeRunId IN"), "the steps are read by their runs' keys in a second query");

        var skipped = (await Repo<ShapeRun>().GetAllAsync(Shaped(steps => steps
                .Where(s => s.Status != "skipped").OrderBy(s => s.StartedAt).Skip(1).Take(2)).AllowClientEvaluation()))
            .ToDictionary(x => x.Id);
        Assert.That(skipped[run.Id].Steps!.Select(s => s.StartedAt), Is.EqualTo(new[] { At(2), At(4) }), "skip and take apply per run");
        Assert.That(skipped[other.Id].Steps!.Select(s => s.StartedAt), Is.EqualTo(new[] { At(3) }));

        _statements.Clear();
        var newest = (await Repo<ShapeRun>().GetAllAsync(Shaped(steps => steps.OrderByDescending(s => s.StartedAt).Take(2))))
            .ToDictionary(x => x.Id);
        Assert.That(newest[run.Id].Steps!.Select(s => s.StartedAt), Is.EqualTo(new[] { At(6), At(5) }));
        Assert.That(newest[other.Id].Steps!.Select(s => s.StartedAt), Is.EqualTo(new[] { At(3), At(2) }));
        Assert.That(_statements, Has.Some.Contains("PER PARTITION LIMIT 2"),
            "ordered as the steps are clustered, each run's limit runs on the cluster");

        var whole = await Repo<ShapeRun>().GetAsync(run.Id, new QueryOptions<ShapeRun>().Include(x => x.Steps));
        Assert.That(whole!.Steps, Has.Count.EqualTo(6), "an include without a shape loads every step");
    }

    [Test]
    public async Task A_relation_whose_key_is_stored_in_another_form_is_counted_on_the_cluster()
    {
        var regions = _db.Resolve<IAsyncRepository<ShapeRegion, ShapeZone>>();
        await regions.AddAsync(new ShapeRegion { Id = ShapeZone.North, Name = "north" });
        await regions.AddAsync(new ShapeRegion { Id = ShapeZone.South, Name = "south" });
        await Add(new ShapeOffice { ShapeRegionId = ShapeZone.North, City = "a" }, new ShapeOffice { ShapeRegionId = ShapeZone.North, City = "b" },
            new ShapeOffice { ShapeRegionId = ShapeZone.South, City = "c" });
        await Commit();

        var zones = new List<ShapeZone> { ShapeZone.North, ShapeZone.South };
        var counted = (await regions.GetMappedAsync(x => new { x.Id, Offices = x.Offices!.Count() },
                new QueryOptions<ShapeRegion>().Where(x => zones.Contains(x.Id))))
            .ToDictionary(x => x.Id, x => x.Offices);

        Assert.That(_statements, Has.Some.Contains("GROUP BY ShapeRegionId"), "partitioned by the foreign key, counted on the cluster");
        Assert.That(counted, Is.EqualTo(new Dictionary<ShapeZone, int> { [ShapeZone.North] = 2, [ShapeZone.South] = 1 }),
            "the cluster answers each zone as its number, which is the member the parent holds");
    }

    [Test]
    public async Task A_relation_is_counted_not_loaded()
    {
        var package = new ShapePackage { Name = "core" };
        var empty = new ShapePackage { Name = "empty" };
        await Add(package, empty);
        await Add(
            new ShapeVersion { ShapePackageId = package.Id, Label = "1.0.0" },
            new ShapeVersion { ShapePackageId = package.Id, Label = "1.1.0", DeletedAt = T0 },
            new ShapeVersion { ShapePackageId = package.Id, Label = "2.0.0" },
            new ShapeVersion { ShapePackageId = package.Id, Label = "2.1.0" });
        await Commit();

        var ids = new List<Guid> { package.Id, empty.Id };
        var all = (await Repo<ShapePackage>().GetMappedAsync(x => new { x.Id, All = x.Versions!.Count(), Loaded = x.Versions != null },
                new QueryOptions<ShapePackage>().Where(x => ids.Contains(x.Id))))
            .ToDictionary(x => x.Id);
        Assert.That(all[package.Id].All, Is.EqualTo(4));
        Assert.That(all[package.Id].Loaded, Is.False, "the versions were counted, not loaded");
        Assert.That(all[empty.Id].All, Is.EqualTo(0), "a package with no version counts zero");
        Assert.That(_statements, Has.Some.Contains("COUNT(*)").And.Some.Contains("GROUP BY ShapePackageId"),
            "partitioned by the foreign key, the versions are counted on the cluster");

        Assert.That(async () => await Repo<ShapePackage>().GetMappedAsync(x => x.Versions!.Count(v => v.DeletedAt == null),
                new QueryOptions<ShapePackage>().Where(x => ids.Contains(x.Id))),
            Throws.InstanceOf<NotSupportedException>().With.Message.Contains("AllowClientEvaluation"),
            "CQL has no null test: that count runs client-side only behind the read's opt-in");

        var live = (await Repo<ShapePackage>().GetMappedAsync(x => new { x.Id, Live = x.Versions!.Count(v => v.DeletedAt == null) },
                new QueryOptions<ShapePackage>().Where(x => ids.Contains(x.Id)).AllowClientEvaluation()))
            .ToDictionary(x => x.Id);
        Assert.That(live[package.Id].Live, Is.EqualTo(3), "the versions not deleted");
        Assert.That(live[empty.Id].Live, Is.EqualTo(0));

        var major = "2.";
        var first = await Repo<ShapePackage>().GetFirstMappedAsync(
            x => new { x.Name, Majors = x.Versions!.LongCount(v => v.Label.StartsWith(major)) },
            new QueryOptions<ShapePackage>().Where(x => x.Id == package.Id).AllowClientEvaluation());
        Assert.That(first!.Majors, Is.EqualTo(2L), "a count's filter reads captured values, and LongCount counts as a long");
    }

    /// <summary>
    ///     A count's filter may test the counted entity's own relations, a collection (<c>s.Logs.Any()</c>) and a reference's
    ///     member (<c>s.Runner.Name == a</c>), negated, null or combined: the related keys are read first, under the read's
    ///     opt-ins (a related read that names no partition scans, behind <c>.AllowFiltering()</c>), and the steps they match
    ///     are counted, never a count that ignored them.
    /// </summary>
    [Test]
    public async Task A_count_s_filter_crosses_the_counted_entity_s_relations()
    {
        var a = "a-" + Guid.NewGuid().ToString("N");
        var b = "b-" + Guid.NewGuid().ToString("N");
        var onA = new ShapeRunner { Name = a };
        var onB = new ShapeRunner { Name = b };
        var build = new ShapeRun { Name = "build" };
        var idle = new ShapeRun { Name = "idle" };
        var deploy = new ShapeRun { Name = "deploy" };
        var logged = new ShapeStep { ShapeRunId = build.Id, Status = "ok", StartedAt = T0.AddMinutes(1), RunnerId = onA.Id };
        var silent = new ShapeStep { ShapeRunId = build.Id, Status = "ok", StartedAt = T0.AddMinutes(2), RunnerId = onB.Id };
        var unassigned = new ShapeStep { ShapeRunId = build.Id, Status = "ok", StartedAt = T0.AddMinutes(3) };
        var shipped = new ShapeStep { ShapeRunId = deploy.Id, Status = "ok", StartedAt = T0.AddMinutes(4), RunnerId = onA.Id };
        await Add(onA, onB);
        await Add(build, idle, deploy);
        await Add(logged, silent, unassigned, shipped);
        await Add(
            new ShapeLog { ShapeStepId = logged.Id, Text = "1" },
            new ShapeLog { ShapeStepId = logged.Id, Text = "2" },
            new ShapeLog { ShapeStepId = unassigned.Id, Text = "3" });
        await Commit();

        var ids = new[] { build.Id, idle.Id, deploy.Id };
        var rows = (await Repo<ShapeRun>().GetMappedAsync(x => new
            {
                x.Id,
                Busy = x.Steps!.Count(s => s.Logs!.Any()),
                Quiet = x.Steps!.Count(s => !s.Logs!.Any()),
                OnA = x.Steps!.Count(s => s.Runner!.Name == a),
                Unassigned = x.Steps!.LongCount(s => s.Runner == null),
                Either = x.Steps!.Count(s => s.Logs!.Any(l => l.Text == "3") || s.Runner!.Name == b),
            }, new QueryOptions<ShapeRun>().Where(x => ids.Contains(x.Id)).AllowFiltering().AllowClientEvaluation()))
            .ToDictionary(x => x.Id);

        Assert.That((rows[build.Id].Busy, rows[build.Id].Quiet, rows[build.Id].OnA, rows[build.Id].Unassigned, rows[build.Id].Either),
            Is.EqualTo((2, 1, 1, 1L, 2)), "the steps with logs, without, on runner a, on none, and with log 3 or on runner b");
        Assert.That((rows[idle.Id].Busy, rows[idle.Id].Quiet, rows[idle.Id].OnA, rows[idle.Id].Unassigned, rows[idle.Id].Either),
            Is.EqualTo((0, 0, 0, 0L, 0)), "a run without steps counts zero");
        Assert.That((rows[deploy.Id].Busy, rows[deploy.Id].Quiet, rows[deploy.Id].OnA, rows[deploy.Id].Unassigned, rows[deploy.Id].Either),
            Is.EqualTo((0, 1, 1, 0L, 0)));
    }

    [Test]
    public async Task Nulls_are_ordered_first_or_last()
    {
        await Add(
            new ShapeCard { Board = "b", Position = 2, Rank = 1 },
            new ShapeCard { Board = "b", Position = null, Rank = 2 },
            new ShapeCard { Board = "b", Position = 1, Rank = 3 });
        await Commit();

        async Task<int?[]> Read(Func<QueryOptions<ShapeCard>, QueryOptions<ShapeCard>> order) =>
            (await Repo<ShapeCard>().GetAllAsync(order(new QueryOptions<ShapeCard>().Where(x => x.Board == "b").AllowClientEvaluation())))
            .Select(x => x.Position).ToArray();

        Assert.That(await Read(o => o.OrderBy(x => x.Position, NullsOrder.First)), Is.EqualTo(new int?[] { null, 1, 2 }));
        Assert.That(await Read(o => o.OrderBy(x => x.Position, NullsOrder.Last)), Is.EqualTo(new int?[] { 1, 2, null }));
        Assert.That(await Read(o => o.OrderByDescending(x => x.Position, NullsOrder.First)), Is.EqualTo(new int?[] { null, 2, 1 }));
        Assert.That(await Read(o => o.OrderByDescending(x => x.Position, NullsOrder.Last)), Is.EqualTo(new int?[] { 2, 1, null }));
        Assert.That(await Read(o => o.OrderBy(x => x.Position, NullsOrder.Last).Take(2)), Is.EqualTo(new int?[] { 1, 2 }),
            "the limit applies after the client-side sort");

        Assert.That(async () => await Repo<ShapeCard>().GetAllAsync(new QueryOptions<ShapeCard>()
                .Where(x => x.Board == "b").OrderBy(x => x.Position, NullsOrder.First)),
            Throws.InstanceOf<NotSupportedException>().With.Message.Contains("ORDER BY clustering keys"),
            "without the opt-in the sort is refused, never ignored");

        var ranks = await Repo<ShapeCard>().GetMappedAsync(x => x.Rank, new QueryOptions<ShapeCard>()
            .Where(x => x.Board == "b").OrderByDescending(x => x.Rank, NullsOrder.First));
        Assert.That(ranks, Is.EqualTo(new[] { 3, 2, 1 }), "a clustering key is never null: CQL's own order honours the placement");
    }

    [Test]
    public async Task A_page_is_read_without_its_total()
    {
        foreach (var rank in Enumerable.Range(1, 25).OrderBy(rank => (rank * 7) % 25))
        {
            await Add(new ShapeCard { Board = "page", Rank = rank });
        }

        await Commit();
        _statements.Clear();

        var page = await Repo<ShapeCard>().GetAllAsync(new QueryOptions<ShapeCard>()
            .Where(x => x.Board == "page").OrderBy(x => x.Rank).Skip(10).Take(10));
        var mapped = await Repo<ShapeCard>().GetMappedAsync(x => x.Rank, new QueryOptions<ShapeCard>()
            .Where(x => x.Board == "page").OrderByDescending(x => x.Rank).Skip(20).Take(10));

        Assert.That(page.Select(x => x.Rank), Is.EqualTo(Enumerable.Range(11, 10)), "rows 11 to 20 of the order");
        Assert.That(mapped, Is.EqualTo(new[] { 5, 4, 3, 2, 1 }));
        Assert.That(_statements, Has.All.Contains("LIMIT ?"), "skip + take rows are fetched, no more");
        Assert.That(_statements, Has.None.Contains("COUNT("), "no count ran");

        Assert.That(async () => await Repo<ShapeCard>().CountAsync(new QueryOptions<ShapeCard>().Take(10)),
            Throws.InstanceOf<NotSupportedException>());
    }

    [Test]
    public async Task Distinct_values_of_two_members()
    {
        await Add(
            new ShapeComment { Repository = "repo-a", IssueNumber = 1, Body = "first" },
            new ShapeComment { Repository = "repo-a", IssueNumber = 1, Body = "again" },
            new ShapeComment { Repository = "repo-a", IssueNumber = 2, Body = "other issue" },
            new ShapeComment { Repository = "elsewhere", IssueNumber = 1, Body = "not matched" });
        await Commit();

        var distinct = (IDistinctReadRepository<ShapeComment>)Repo<ShapeComment>();
        var pairs = await distinct.GetDistinctAsync(x => new { x.Repository, x.IssueNumber }, x => x.Repository == "repo-a");
        Assert.That(pairs.Select(pair => (pair.Repository, pair.IssueNumber)),
            Is.EquivalentTo(new[] { ("repo-a", 1), ("repo-a", 2) }), "each combination once");
        Assert.That(_statements, Has.Some.Contains("GROUP BY Repository, IssueNumber"), "a primary-key prefix groups on the cluster");

        var repositories = await distinct.GetDistinctAsync(x => x.Repository);
        Assert.That(repositories, Is.EquivalentTo(new[] { "repo-a", "elsewhere" }));

        Assert.That(async () => await distinct.GetDistinctAsync(x => x.Body, x => x.Repository == "repo-a"),
            Throws.InstanceOf<NotSupportedException>().With.Message.Contains("AllowClientEvaluation"),
            "outside a primary-key prefix the values are deduplicated client-side, behind the opt-in");
        var bodies = await distinct.GetDistinctAsync(x => x.IssueNumber, x => x.Repository == "repo-a",
            new QueryOptions<ShapeComment>().AllowClientEvaluation());
        Assert.That(bodies, Is.EquivalentTo(new[] { 1, 2 }));
    }

    /// <remarks>
    ///     A ticket's queue key is a regular column, so the include cannot restrict the tickets by it: they are read whole and
    ///     kept client-side, which is a scan (<c>.AllowFiltering()</c>) and client work (<c>.AllowClientEvaluation()</c>).
    /// </remarks>
    [Test]
    public async Task An_include_whose_foreign_key_is_a_regular_column_scans_only_behind_both_opt_ins()
    {
        var support = new ShapeQueue { Name = "support" };
        var sales = new ShapeQueue { Name = "sales" };
        await Add(support, sales);
        await Add(
            new ShapeTicket { Desk = "east", ShapeQueueId = support.Id, Title = "t1" },
            new ShapeTicket { Desk = "west", ShapeQueueId = support.Id, Title = "t2" },
            new ShapeTicket { Desk = "east", ShapeQueueId = sales.Id, Title = "t3" });
        await Commit();

        QueryOptions<ShapeQueue> Including() => new QueryOptions<ShapeQueue>().Where(x => x.Id == support.Id).Include(x => x.Tickets);

        Assert.That(async () => await Repo<ShapeQueue>().GetAllAsync(Including()),
            Throws.InstanceOf<NotSupportedException>().With.Message.Contains("AllowClientEvaluation"));
        Assert.That(async () => await Repo<ShapeQueue>().GetAllAsync(Including().AllowClientEvaluation()),
            Throws.InstanceOf<NotSupportedException>().With.Message.Contains("AllowFiltering"),
            "the tickets are not read by a partition: the read is a scan of the table");

        var queue = (await Repo<ShapeQueue>().GetAllAsync(Including().AllowClientEvaluation().AllowFiltering())).Single();
        Assert.That(queue.Tickets!.Select(x => x.Title).Order(), Is.EqualTo(new[] { "t1", "t2" }));
    }

    /// <remarks>An include over 250 parents reads the related rows in chunks, each a query of its own.</remarks>
    [Test]
    public async Task An_include_over_more_parents_than_a_chunk_reads_in_chunks()
    {
        const int count = 250;
        var runs = Enumerable.Range(0, count).Select(index => new ShapeRun { Name = $"run-{index:D3}" }).ToArray();
        await Add(runs);
        await Add(runs.Select(run => Step(run, "success", 1)).ToArray());
        await Commit();
        using var executed = new ExecutedCql();

        var loaded = (await Repo<ShapeRun>().GetAllAsync(new QueryOptions<ShapeRun>().Include(x => x.Steps))).ToList();

        Assert.That(loaded, Has.Count.EqualTo(count));
        Assert.That(loaded.Select(run => run.Steps!.Count), Is.All.EqualTo(1), "every run has its step");
        var reads = executed.On("shapestep").Where(statement => statement.IsRead).ToList();
        Assert.That(reads, Has.Count.EqualTo(3), "250 keys in chunks of 100");
        Assert.That(reads.Select(statement => statement.Markers), Has.All.LessThanOrEqualTo(100));
    }

    private static ShapeStep Step(ShapeRun run, string status, int minute) =>
        new() { ShapeRunId = run.Id, Status = status, StartedAt = At(minute) };

    private static DateTime At(int minute) => T0.AddMinutes(minute);

    /// <summary>Records the statements the engine logs.</summary>
    private sealed class StatementLog(ConcurrentQueue<string> statements) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, statements);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<string> statements) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (category.EndsWith(".Command", StringComparison.Ordinal))
                {
                    statements.Enqueue(formatter(state, exception));
                }
            }
        }
    }
}
