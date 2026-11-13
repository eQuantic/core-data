using System.Collections.Concurrent;
using eQuantic.Core.Data.MongoDb.Repository;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Read;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>
///     The read shapes against a real MongoDB: a shaped include as a <c>$lookup</c> with a pipeline, relation counts as
///     a <c>$lookup</c> with <c>$count</c>, nulls placed against MongoDB's own order, a page without its total, and
///     distinct values as a <c>$group</c>.
/// </summary>
[TestFixture]
public sealed class MongoReadShapesTests : MongoIntegrationTest
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private ConcurrentQueue<BsonDocument> _commands = null!;
    private MongoClient? _client;
    private MongoTestDatabase _db = null!;

    [SetUp]
    public void OpenDatabase()
    {
        _commands = new ConcurrentQueue<BsonDocument>();
        var settings = MongoTestServer.Client.Settings.Clone();
        var commands = _commands;
        settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(started =>
        {
            if (started.CommandName is "find" or "aggregate" or "count" or "distinct")
            {
                commands.Enqueue(BsonDocument.Parse(started.Command.ToJson()));
            }
        });
        _client = new MongoClient(settings);
        _db = new MongoTestDatabase(_client, "test_" + Guid.NewGuid().ToString("N"), []);
    }

    [TearDown]
    public void CloseDatabase()
    {
        (_db as IDisposable)?.Dispose();
        _client?.Dispose();
    }

    private IAsyncRepository<TEntity, string> Repo<TEntity>() where TEntity : class, IEntity<string> =>
        _db.Resolve<IAsyncRepository<TEntity, string>>();

    private Task Commit() => _db.Resolve<MongoDefaultUnitOfWork>().CommitAsync();

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
        var run = new ShapeRun { Name = "build" };
        var other = new ShapeRun { Name = "deploy" };
        await Add(run, other);
        await Add(
            Step(run, "success", 1), Step(run, "success", 2), Step(run, "skipped", 3), Step(run, "failure", 4), Step(run, "skipped", 5),
            Step(run, "success", 6), Step(other, "success", 1), Step(other, "skipped", 2), Step(other, "success", 3));
        await Commit();

        var ids = new[] { run.Id, other.Id };
        var runs = (await Repo<ShapeRun>().GetAllAsync(new QueryOptions<ShapeRun>()
                .Where(x => ids.Contains(x.Id))
                .Include(x => x.Steps, steps => steps.Where(s => s.Status != "skipped").OrderByDescending(s => s.StartedAt).Take(3))))
            .ToDictionary(x => x.Id);

        Assert.That(runs[run.Id].Steps!.Select(s => s.StartedAt), Is.EqualTo(new[] { At(6), At(4), At(2) }),
            "the three newest steps that were not skipped, newest first");
        Assert.That(runs[other.Id].Steps!.Select(s => s.StartedAt), Is.EqualTo(new[] { At(3), At(1) }));
        Assert.That(runs[run.Id].Name, Is.EqualTo("build"), "the run's own fields survive the join");

        var skipped = (await Repo<ShapeRun>().GetAllAsync(new QueryOptions<ShapeRun>()
                .Where(x => ids.Contains(x.Id))
                .Include(x => x.Steps, steps => steps.Where(s => s.Status != "skipped").OrderBy(s => s.StartedAt).Skip(1).Take(2))))
            .ToDictionary(x => x.Id);
        Assert.That(skipped[run.Id].Steps!.Select(s => s.StartedAt), Is.EqualTo(new[] { At(2), At(4) }), "skip and take apply per run");
        Assert.That(skipped[other.Id].Steps!.Select(s => s.StartedAt), Is.EqualTo(new[] { At(3) }));

        var lookup = _commands.Last()["pipeline"].AsBsonArray.Select(stage => stage.AsBsonDocument)
            .Single(stage => stage.Contains("$lookup"))["$lookup"].AsBsonDocument;
        Assert.That(lookup["pipeline"].AsBsonArray.Select(stage => stage.AsBsonDocument.GetElement(0).Name),
            Is.EqualTo(new[] { "$match", "$sort", "$skip", "$limit" }), "the shape runs inside the $lookup, on the server");

        var whole = await Repo<ShapeRun>().GetAsync(run.Id, new QueryOptions<ShapeRun>().Include(x => x.Steps));
        Assert.That(whole!.Steps, Has.Count.EqualTo(6), "an include without a shape still loads every step");
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

        var ids = new[] { package.Id, empty.Id };
        var rows = (await Repo<ShapePackage>().GetMappedAsync(x => new
            {
                x.Id,
                Live = x.Versions!.Count(v => v.DeletedAt == null),
                All = x.Versions!.Count(),
                Loaded = x.Versions != null,
            }, new QueryOptions<ShapePackage>().Where(x => ids.Contains(x.Id))))
            .ToDictionary(x => x.Id);

        Assert.That(rows[package.Id].Live, Is.EqualTo(3), "the versions not deleted");
        Assert.That(rows[package.Id].All, Is.EqualTo(4));
        Assert.That(rows[package.Id].Loaded, Is.False, "the versions were counted, not loaded");
        Assert.That(rows[empty.Id].Live, Is.EqualTo(0), "a package with no version counts zero");

        var lookups = _commands.Last()["pipeline"].AsBsonArray.Select(stage => stage.AsBsonDocument)
            .Where(stage => stage.Contains("$lookup")).ToList();
        Assert.That(lookups, Has.Count.EqualTo(2));
        Assert.That(lookups.Select(stage => stage["$lookup"]["pipeline"].AsBsonArray.Last().AsBsonDocument.GetElement(0).Name),
            Is.All.EqualTo("$count"), "the server counts inside the lookup");

        var major = "2.";
        var first = await Repo<ShapePackage>().GetFirstMappedAsync(
            x => new { x.Name, Majors = x.Versions!.LongCount(v => v.Label.StartsWith(major)) },
            new QueryOptions<ShapePackage>().Where(x => x.Id == package.Id));
        Assert.That(first!.Majors, Is.EqualTo(2L), "a count's filter reads captured values, and LongCount counts as a long");
    }

    [Test]
    public async Task Nulls_are_ordered_first_or_last()
    {
        var board = "board-" + Guid.NewGuid().ToString("N");
        await Add(
            new ShapeCard { Board = board, Position = 2, Rank = 1 },
            new ShapeCard { Board = board, Position = null, Rank = 2 },
            new ShapeCard { Board = board, Position = 1, Rank = 3 });
        await Commit();

        // A document written by another tool may lack the field: MongoDB sorts it as a null, and so does the provider.
        var missing = new BsonDocument { { "_id", Guid.NewGuid().ToString("N") }, { "Board", board }, { "Rank", 4 } };
        await _db.Database.GetCollection<BsonDocument>("ShapeCard").InsertOneAsync(missing);

        async Task<int?[]> Read(Func<QueryOptions<ShapeCard>, QueryOptions<ShapeCard>> order) =>
            (await Repo<ShapeCard>().GetAllAsync(order(new QueryOptions<ShapeCard>().Where(x => x.Board == board))))
            .Select(x => x.Position).ToArray();

        Assert.That(await Read(o => o.OrderBy(x => x.Position, NullsOrder.First)), Is.EqualTo(new int?[] { null, null, 1, 2 }));
        Assert.That(await Read(o => o.OrderBy(x => x.Position, NullsOrder.Last)), Is.EqualTo(new int?[] { 1, 2, null, null }));
        Assert.That(await Read(o => o.OrderByDescending(x => x.Position, NullsOrder.First)), Is.EqualTo(new int?[] { null, null, 2, 1 }));
        Assert.That(await Read(o => o.OrderByDescending(x => x.Position, NullsOrder.Last)), Is.EqualTo(new int?[] { 2, 1, null, null }));
        Assert.That(await Read(o => o.OrderBy(x => x.Position, NullsOrder.Last).ThenBy(x => x.Rank)), Is.EqualTo(new int?[] { 1, 2, null, null }));
    }

    [Test]
    public async Task A_page_is_read_without_its_total()
    {
        var board = "page-" + Guid.NewGuid().ToString("N");
        foreach (var rank in Enumerable.Range(1, 25).OrderBy(rank => (rank * 7) % 25))
        {
            await Add(new ShapeCard { Board = board, Rank = rank });
        }

        await Commit();
        _commands.Clear();

        var page = await Repo<ShapeCard>().GetAllAsync(new QueryOptions<ShapeCard>()
            .Where(x => x.Board == board).OrderBy(x => x.Rank).Skip(10).Take(10));
        var mapped = await Repo<ShapeCard>().GetMappedAsync(x => x.Rank, new QueryOptions<ShapeCard>()
            .Where(x => x.Board == board).OrderByDescending(x => x.Rank).Skip(20).Take(10));

        Assert.That(page.Select(x => x.Rank), Is.EqualTo(Enumerable.Range(11, 10)), "rows 11 to 20 of the order");
        Assert.That(mapped, Is.EqualTo(new[] { 5, 4, 3, 2, 1 }));
        Assert.That(_commands, Has.Count.EqualTo(2), "one command per read");
        Assert.That(_commands.Select(command => command.ToJson()), Has.None.Contains("$count").And.None.Contains("\"count\""),
            "no count ran");

        Assert.That(async () => await Repo<ShapeCard>().CountAsync(new QueryOptions<ShapeCard>().Take(10)),
            Throws.InstanceOf<NotSupportedException>(), "a count refuses a window instead of counting it");
    }

    [Test]
    public async Task Distinct_values_of_two_members()
    {
        var repository = "repo-" + Guid.NewGuid().ToString("N");
        await Add(
            new ShapeComment { Repository = repository, IssueNumber = 1, Body = "first" },
            new ShapeComment { Repository = repository, IssueNumber = 1, Body = "again" },
            new ShapeComment { Repository = repository, IssueNumber = 2, Body = "other issue" },
            new ShapeComment { Repository = "elsewhere", IssueNumber = 1, Body = "not matched" });
        await Commit();

        var distinct = (IDistinctReadRepository<ShapeComment>)Repo<ShapeComment>();
        var pairs = await distinct.GetDistinctAsync(x => new { x.Repository, x.IssueNumber }, x => x.Repository == repository);
        Assert.That(pairs.Select(pair => (pair.Repository, pair.IssueNumber)),
            Is.EquivalentTo(new[] { (repository, 1), (repository, 2) }), "each combination once");

        var issues = await distinct.GetDistinctAsync(x => x.IssueNumber, x => x.Repository == repository);
        Assert.That(issues, Is.EquivalentTo(new[] { 1, 2 }));
        Assert.That(_commands.Last()["pipeline"].ToJson(), Does.Contain("$group"), "the server deduplicates");

        Assert.That(async () => await distinct.GetDistinctAsync(x => x.IssueNumber, null,
                new QueryOptions<ShapeComment>().OrderBy(x => x.IssueNumber)),
            Throws.InstanceOf<NotSupportedException>());
    }

    private static ShapeStep Step(ShapeRun run, string status, int minute) =>
        new() { ShapeRunId = run.Id, Status = status, StartedAt = At(minute) };

    private static DateTime At(int minute) => T0.AddMinutes(minute);
}
