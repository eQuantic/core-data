using System.Collections.Concurrent;
using eQuantic.Core.Data.CosmosDb.Extensions;
using eQuantic.Core.Data.CosmosDb.Repository;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Read;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.CosmosDb.Tests;

/// <summary>
///     The read shapes against the Cosmos DB emulator: includes read from the related documents with a second query,
///     relation counts grouped on the server, nulls placed against Cosmos DB's own order by splitting the read, a page
///     without its total (<c>OFFSET</c>/<c>LIMIT</c>), and <c>SELECT DISTINCT VALUE</c>. The fixture runs its own client,
///     whose handler records every query text the SDK sends.
/// </summary>
[TestFixture]
public sealed class CosmosReadShapesTests
{
    // The well-known emulator account key (the same for every emulator).
    private const string EmulatorKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly QueryCapture _queries = new();
    private CosmosClient? _client;
    private ServiceProvider? _provider;
    private IServiceScope _scope = null!;
    private string _partition = null!;

    [OneTimeSetUp]
    public void BuildProvider()
    {
        CosmosTestServer.EnsureAvailable();
        var shared = CosmosTestServer.Provider.GetRequiredService<CosmosClient>();
        var model = CosmosTestServer.Provider.GetRequiredService<CosmosModel>();
        _client = new CosmosClient(shared.Endpoint.ToString(), EmulatorKey, new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            LimitToEndpoint = true,
            Serializer = new CosmosEntitySerializer(CosmosEntitySerializer.BuildOptions(model)),
            RequestTimeout = TimeSpan.FromMinutes(2),
            CustomHandlers = { _queries },
        });

        var services = new ServiceCollection();
        services.AddSingleton(_client);
        services.AddSingleton(model);
        services.AddCosmosDatabase("emulator", CosmosTestServer.DatabaseName, _ => { });
        services.AddCosmosRepositories();
        _provider = services.BuildServiceProvider();
    }

    [OneTimeTearDown]
    public void DisposeProvider()
    {
        _provider?.Dispose();
        _client?.Dispose();
    }

    [SetUp]
    public void OpenScope()
    {
        CosmosTestServer.EnsureAvailable();
        _partition = "p" + Guid.NewGuid().ToString("N");
        _scope = _provider!.CreateScope();
    }

    [TearDown]
    public void CloseScope() => _scope?.Dispose();

    private IAsyncRepository<TEntity, string> Repo<TEntity>() where TEntity : class, IEntity<string> =>
        _scope.ServiceProvider.GetRequiredService<IAsyncRepository<TEntity, string>>();

    private Task Commit() => _scope.ServiceProvider.GetRequiredService<CosmosDefaultUnitOfWork>().CommitAsync();

    private async Task Add<TEntity>(params TEntity[] entities) where TEntity : class, IEntity<string>
    {
        foreach (var entity in entities)
        {
            await Repo<TEntity>().AddAsync(entity);
        }
    }

    [Test]
    public async Task An_include_is_filtered_ordered_and_limited_per_parent()
    {
        var run = new ShapeRun { Category = _partition, Name = "build" };
        var other = new ShapeRun { Category = _partition, Name = "deploy" };
        await Add(run, other);
        await Add(
            Step(run, "success", 1), Step(run, "success", 2), Step(run, "skipped", 3), Step(run, "failure", 4), Step(run, "skipped", 5),
            Step(run, "success", 6), Step(other, "success", 1), Step(other, "skipped", 2), Step(other, "success", 3));
        await Commit();

        var ids = new List<string> { run.Id, other.Id };
        _queries.Clear();
        var runs = (await Repo<ShapeRun>().GetAllAsync(new QueryOptions<ShapeRun>()
                .Where(x => x.Category == _partition && ids.Contains(x.Id))
                .Include(x => x.Steps, steps => steps.Where(s => s.Status != "skipped").OrderByDescending(s => s.StartedAt).Take(3))))
            .ToDictionary(x => x.Id);

        Assert.That(runs[run.Id].Steps!.Select(s => s.StartedAt), Is.EqualTo(new[] { At(6), At(4), At(2) }),
            "the three newest steps that were not skipped, newest first");
        Assert.That(runs[other.Id].Steps!.Select(s => s.StartedAt), Is.EqualTo(new[] { At(3), At(1) }));
        Assert.That(_queries.Texts, Has.Some.Matches<string>(text =>
                text.Contains("shapeRunId") && text.Contains(" IN (") && text.Contains("skipped")),
            "the steps are read by their runs' keys in a second query, the filter in its WHERE");

        var skipped = (await Repo<ShapeRun>().GetAllAsync(new QueryOptions<ShapeRun>()
                .Where(x => x.Category == _partition && ids.Contains(x.Id))
                .Include(x => x.Steps, steps => steps.Where(s => s.Status != "skipped").OrderBy(s => s.StartedAt).Skip(1).Take(2))))
            .ToDictionary(x => x.Id);
        Assert.That(skipped[run.Id].Steps!.Select(s => s.StartedAt), Is.EqualTo(new[] { At(2), At(4) }), "skip and take apply per run");
        Assert.That(skipped[other.Id].Steps!.Select(s => s.StartedAt), Is.EqualTo(new[] { At(3) }));

        var whole = await Repo<ShapeRun>().GetAsync(run.Id, new QueryOptions<ShapeRun>().Include(x => x.Steps));
        Assert.That(whole!.Steps, Has.Count.EqualTo(6), "an include without a shape loads every step");

        var step = (await Repo<ShapeStep>().GetAllAsync(new QueryOptions<ShapeStep>()
            .Where(x => x.Category == _partition && x.ShapeRunId == other.Id && x.Status == "skipped")
            .Include(nameof(ShapeStep.ShapeRun)))).Single();
        Assert.That(step.ShapeRun?.Name, Is.EqualTo("deploy"), "a reference include reads the run by its key");
    }

    [Test]
    public async Task A_relation_is_counted_not_loaded()
    {
        var package = new ShapePackage { Category = _partition, Name = "core" };
        var empty = new ShapePackage { Category = _partition, Name = "empty" };
        await Add(package, empty);
        await Add(
            new ShapeVersion { Category = _partition, ShapePackageId = package.Id, Label = "1.0.0" },
            new ShapeVersion { Category = _partition, ShapePackageId = package.Id, Label = "1.1.0", DeletedAt = T0 },
            new ShapeVersion { Category = _partition, ShapePackageId = package.Id, Label = "2.0.0" },
            new ShapeVersion { Category = _partition, ShapePackageId = package.Id, Label = "2.1.0" });
        await Commit();

        var ids = new List<string> { package.Id, empty.Id };
        _queries.Clear();
        var rows = (await Repo<ShapePackage>().GetMappedAsync(x => new
            {
                x.Id,
                Live = x.Versions!.Count(v => v.DeletedAt == null),
                All = x.Versions!.Count(),
                Loaded = x.Versions != null,
            }, new QueryOptions<ShapePackage>().Where(x => x.Category == _partition && ids.Contains(x.Id))))
            .ToDictionary(x => x.Id);

        Assert.That(rows[package.Id].Live, Is.EqualTo(3), "the versions not deleted");
        Assert.That(rows[package.Id].All, Is.EqualTo(4));
        Assert.That(rows[package.Id].Loaded, Is.False, "the versions were counted, not loaded");
        Assert.That(rows[empty.Id].Live, Is.EqualTo(0), "a package with no version counts zero");
        Assert.That(_queries.Texts.Where(text => text.Contains("GROUP BY")).ToList(), Has.Count.EqualTo(2),
            "each count is one grouped query on the server");

        var major = "2.";
        var first = await Repo<ShapePackage>().GetFirstMappedAsync(
            x => new { x.Name, Majors = x.Versions!.LongCount(v => v.Label.StartsWith(major)) },
            new QueryOptions<ShapePackage>().Where(x => x.Category == _partition && x.Id == package.Id));
        Assert.That(first!.Majors, Is.EqualTo(2L), "a count's filter reads captured values, and LongCount counts as a long");
    }

    /// <summary>
    ///     A count's filter may test the counted entity's own relations, a collection (<c>s.Logs.Any()</c>) and a reference's
    ///     member (<c>s.Runner.Name == a</c>), negated, null or combined: the store counts with them, as a filter across
    ///     relations reads them, and never answers a count that ignored them.
    /// </summary>
    [Test]
    public async Task A_count_s_filter_crosses_the_counted_entity_s_relations()
    {
        var a = "a-" + Guid.NewGuid().ToString("N");
        var b = "b-" + Guid.NewGuid().ToString("N");
        var onA = new ShapeRunner { Category = _partition, Name = a };
        var onB = new ShapeRunner { Category = _partition, Name = b };
        var build = new ShapeRun { Category = _partition, Name = "build" };
        var idle = new ShapeRun { Category = _partition, Name = "idle" };
        var deploy = new ShapeRun { Category = _partition, Name = "deploy" };
        var logged = new ShapeStep { Category = _partition, ShapeRunId = build.Id, Status = "ok", StartedAt = T0.AddMinutes(1), RunnerId = onA.Id };
        var silent = new ShapeStep { Category = _partition, ShapeRunId = build.Id, Status = "ok", StartedAt = T0.AddMinutes(2), RunnerId = onB.Id };
        var unassigned = new ShapeStep { Category = _partition, ShapeRunId = build.Id, Status = "ok", StartedAt = T0.AddMinutes(3) };
        var shipped = new ShapeStep { Category = _partition, ShapeRunId = deploy.Id, Status = "ok", StartedAt = T0.AddMinutes(4), RunnerId = onA.Id };
        await Add(onA, onB);
        await Add(build, idle, deploy);
        await Add(logged, silent, unassigned, shipped);
        await Add(
            new ShapeLog { Category = _partition, ShapeStepId = logged.Id, Text = "1" },
            new ShapeLog { Category = _partition, ShapeStepId = logged.Id, Text = "2" },
            new ShapeLog { Category = _partition, ShapeStepId = unassigned.Id, Text = "3" });
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
            }, new QueryOptions<ShapeRun>().Where(x => x.Category == _partition && ids.Contains(x.Id))))
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
            new ShapeCard { Category = _partition, Board = "b", Position = 2, Rank = 1 },
            new ShapeCard { Category = _partition, Board = "b", Position = null, Rank = 2 },
            new ShapeCard { Category = _partition, Board = "b", Position = 1, Rank = 3 });
        await Commit();

        async Task<int?[]> Read(Func<QueryOptions<ShapeCard>, QueryOptions<ShapeCard>> order) =>
            (await Repo<ShapeCard>().GetAllAsync(order(new QueryOptions<ShapeCard>().Where(x => x.Category == _partition))))
            .Select(x => x.Position).ToArray();

        Assert.That(await Read(o => o.OrderBy(x => x.Position, NullsOrder.First)), Is.EqualTo(new int?[] { null, 1, 2 }));
        Assert.That(await Read(o => o.OrderBy(x => x.Position, NullsOrder.Last)), Is.EqualTo(new int?[] { 1, 2, null }));
        Assert.That(await Read(o => o.OrderByDescending(x => x.Position, NullsOrder.First)), Is.EqualTo(new int?[] { null, 2, 1 }));
        Assert.That(await Read(o => o.OrderByDescending(x => x.Position, NullsOrder.Last)), Is.EqualTo(new int?[] { 2, 1, null }));

        // The window carries across the two parts of a split read.
        Assert.That(await Read(o => o.OrderBy(x => x.Position, NullsOrder.Last).Skip(1).Take(2)), Is.EqualTo(new int?[] { 2, null }));
        Assert.That(await Read(o => o.OrderBy(x => x.Position, NullsOrder.Last).Skip(2)), Is.EqualTo(new int?[] { null }));
        Assert.That(await Read(o => o.OrderBy(x => x.Position, NullsOrder.Last).Skip(3)), Is.Empty);

        Assert.That(async () => await Read(o => o.OrderBy(x => x.Rank).ThenBy(x => x.Position, NullsOrder.Last)),
            Throws.InstanceOf<NotSupportedException>(), "a placement on a later sort cannot split the read, and is refused");
    }

    [Test]
    public async Task A_page_is_read_without_its_total()
    {
        foreach (var rank in Enumerable.Range(1, 25).OrderBy(rank => (rank * 7) % 25))
        {
            await Add(new ShapeCard { Category = _partition, Board = "page", Rank = rank });
        }

        await Commit();
        _queries.Clear();

        var page = await Repo<ShapeCard>().GetAllAsync(new QueryOptions<ShapeCard>()
            .Where(x => x.Category == _partition).OrderBy(x => x.Rank).Skip(10).Take(10));
        var mapped = await Repo<ShapeCard>().GetMappedAsync(x => x.Rank, new QueryOptions<ShapeCard>()
            .Where(x => x.Category == _partition).OrderByDescending(x => x.Rank).Skip(20).Take(10));

        Assert.That(page.Select(x => x.Rank), Is.EqualTo(Enumerable.Range(11, 10)), "rows 11 to 20 of the order");
        Assert.That(mapped, Is.EqualTo(new[] { 5, 4, 3, 2, 1 }));
        Assert.That(_queries.Texts, Has.Some.Contains("OFFSET"), "the page is read with OFFSET/LIMIT");
        Assert.That(_queries.Texts, Has.None.Contains("COUNT("), "no count ran");

        Assert.That(async () => await Repo<ShapeCard>().CountAsync(new QueryOptions<ShapeCard>().Take(10)),
            Throws.InstanceOf<NotSupportedException>());
    }

    [Test]
    public async Task Distinct_values_of_two_members()
    {
        await Add(
            new ShapeComment { Category = _partition, Repository = "repo-a", IssueNumber = 1, Body = "first" },
            new ShapeComment { Category = _partition, Repository = "repo-a", IssueNumber = 1, Body = "again" },
            new ShapeComment { Category = _partition, Repository = "repo-a", IssueNumber = 2, Body = "other issue" },
            new ShapeComment { Category = _partition, Repository = "elsewhere", IssueNumber = 1, Body = "not matched" });
        await Commit();

        var distinct = (IDistinctReadRepository<ShapeComment>)Repo<ShapeComment>();
        _queries.Clear();
        var pairs = await distinct.GetDistinctAsync(x => new { x.Repository, x.IssueNumber },
            x => x.Category == _partition && x.Repository == "repo-a");
        Assert.That(pairs.Select(pair => (pair.Repository, pair.IssueNumber)),
            Is.EquivalentTo(new[] { ("repo-a", 1), ("repo-a", 2) }), "each combination once");
        Assert.That(_queries.Texts, Has.Some.Contains("DISTINCT"), "the server deduplicates");

        var issues = await distinct.GetDistinctAsync(x => x.IssueNumber, x => x.Category == _partition && x.Repository == "repo-a");
        Assert.That(issues, Is.EquivalentTo(new[] { 1, 2 }));
    }

    private ShapeStep Step(ShapeRun run, string status, int minute) =>
        new() { Category = _partition, ShapeRunId = run.Id, Status = status, StartedAt = At(minute) };

    private static DateTime At(int minute) => T0.AddMinutes(minute);

    /// <summary>Records the query text of every query request the SDK sends.</summary>
    private sealed class QueryCapture : RequestHandler
    {
        private readonly ConcurrentQueue<string> _texts = new();

        public IReadOnlyList<string> Texts => _texts.ToArray();

        public void Clear() => _texts.Clear();

        public override async Task<ResponseMessage> SendAsync(RequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.Headers["x-ms-documentdb-isquery"] is not null
                && request.Content is { } content)
            {
                using var buffer = new MemoryStream();
                await content.CopyToAsync(buffer, cancellationToken);
                var bytes = buffer.ToArray();
                _texts.Enqueue(System.Text.Encoding.UTF8.GetString(bytes));
                request.Content = new MemoryStream(bytes);
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
