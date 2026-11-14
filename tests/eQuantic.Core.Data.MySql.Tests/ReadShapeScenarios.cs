using System.Collections.Concurrent;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Read;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace eQuantic.Core.Data.MySql.Tests;

/// <summary>Records the statements a database runs, from the engine's command log.</summary>
internal sealed class CommandCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _statements = new();

    /// <summary>The statements logged since the last <see cref="Clear" />.</summary>
    public IReadOnlyList<string> Statements => _statements.ToArray();

    /// <summary>Forgets the statements logged so far.</summary>
    public void Clear() => _statements.Clear();

    /// <summary>Registers the capture as the logger factory of a test database.</summary>
    public void Register(IServiceCollection services) =>
        services.AddSingleton<ILoggerFactory>(_ =>
        {
            var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information));
            factory.AddProvider(this);
            return factory;
        });

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _statements);

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

/// <summary>The repositories a scenario reads and writes, the commit, and the statements the database ran.</summary>
internal sealed class ShapeStore(Func<Type, object> resolve, Func<Task> commit, CommandCapture commands)
{
    public IAsyncRepository<TEntity, Guid> Repo<TEntity>() where TEntity : class, IEntity<Guid> =>
        (IAsyncRepository<TEntity, Guid>)resolve(typeof(IAsyncRepository<TEntity, Guid>));

    public Task Commit() => commit();

    public CommandCapture Commands => commands;

    public async Task Add<TEntity>(params TEntity[] entities) where TEntity : class, IEntity<Guid>
    {
        foreach (var entity in entities)
        {
            await Repo<TEntity>().AddAsync(entity);
        }
    }
}

/// <summary>
///     The read-shape scenarios every relational dialect has to pass — the same assertions, each run against its own
///     server: a shaped include, a relation count, nulls placed first or last, a page without its total and distinct
///     values of several members.
/// </summary>
internal static class ReadShapeScenarios
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The run's steps are the three newest of those not skipped, newest first, and each run gets its own.</summary>
    public static async Task TheLastThreeNewestFirst(ShapeStore store)
    {
        var run = new ShapeRun { Name = "build" };
        var other = new ShapeRun { Name = "deploy" };
        await store.Add(run, other);
        await store.Add(
            Step(run, "success", 1), Step(run, "success", 2), Step(run, "skipped", 3), Step(run, "failure", 4), Step(run, "skipped", 5),
            Step(run, "success", 6), Step(other, "success", 1), Step(other, "skipped", 2), Step(other, "success", 3));
        await store.Commit();

        var ids = new[] { run.Id, other.Id };
        var runs = (await store.Repo<ShapeRun>().GetAllAsync(new QueryOptions<ShapeRun>()
                .Where(x => ids.Contains(x.Id))
                .Include(x => x.Steps, steps => steps
                    .Where(s => s.Status != "skipped")
                    .OrderByDescending(s => s.StartedAt)
                    .Take(3))))
            .ToDictionary(x => x.Id);

        Assert.That(runs[run.Id].Steps!.Select(s => s.StartedAt), Is.EqualTo(new[] { At(6), At(4), At(2) }),
            "the three newest steps that were not skipped, newest first");
        Assert.That(runs[other.Id].Steps!.Select(s => s.StartedAt), Is.EqualTo(new[] { At(3), At(1) }),
            "each run keeps its own steps, up to the limit");

        var skipped = (await store.Repo<ShapeRun>().GetAllAsync(new QueryOptions<ShapeRun>()
                .Where(x => ids.Contains(x.Id))
                .Include(x => x.Steps, steps => steps.Where(s => s.Status != "skipped").OrderBy(s => s.StartedAt).Skip(1).Take(2))))
            .ToDictionary(x => x.Id);
        Assert.That(skipped[run.Id].Steps!.Select(s => s.StartedAt), Is.EqualTo(new[] { At(2), At(4) }), "skip and take apply per run");
        Assert.That(skipped[other.Id].Steps!.Select(s => s.StartedAt), Is.EqualTo(new[] { At(3) }));

        var whole = await store.Repo<ShapeRun>().GetAsync(run.Id, new QueryOptions<ShapeRun>().Include(x => x.Steps));
        Assert.That(whole!.Steps, Has.Count.EqualTo(6), "an include without a shape still loads every step");
    }

    /// <summary>The store counts the versions not deleted, and none is loaded.</summary>
    public static async Task CountedNotLoaded(ShapeStore store)
    {
        var package = new ShapePackage { Name = "core" };
        var empty = new ShapePackage { Name = "empty" };
        await store.Add(package, empty);
        await store.Add(
            new ShapeVersion { ShapePackageId = package.Id, Label = "1.0.0" },
            new ShapeVersion { ShapePackageId = package.Id, Label = "1.1.0", DeletedAt = T0 },
            new ShapeVersion { ShapePackageId = package.Id, Label = "2.0.0" },
            new ShapeVersion { ShapePackageId = package.Id, Label = "2.1.0" });
        await store.Commit();

        var ids = new[] { package.Id, empty.Id };
        var rows = (await store.Repo<ShapePackage>().GetMappedAsync(x => new
            {
                x.Id,
                Live = x.Versions!.Count(v => v.DeletedAt == null),
                All = x.Versions!.Count(),
                Loaded = x.Versions != null,
            }, new QueryOptions<ShapePackage>().Where(x => ids.Contains(x.Id))))
            .ToDictionary(x => x.Id);

        Assert.That(rows[package.Id].Live, Is.EqualTo(3), "the versions not deleted");
        Assert.That(rows[package.Id].All, Is.EqualTo(4), "a count without a filter counts every version");
        Assert.That(rows[package.Id].Loaded, Is.False, "the versions were counted, not loaded");
        Assert.That(rows[empty.Id].Live, Is.EqualTo(0), "a package with no version counts zero");

        var major = "2.";
        var first = await store.Repo<ShapePackage>().GetFirstMappedAsync(
            x => new { x.Name, Majors = x.Versions!.LongCount(v => v.Label.StartsWith(major)) },
            new QueryOptions<ShapePackage>().Where(x => x.Id == package.Id));
        Assert.That(first!.Majors, Is.EqualTo(2L), "a count's filter reads captured values, and LongCount counts as a long");
    }

    /// <summary>
    ///     A count's filter may test the counted entity's own relations, a collection (<c>s.Logs.Any()</c>) and a reference's
    ///     member (<c>s.Runner.Name == a</c>), negated, null or combined: the store counts with them, as a filter across
    ///     relations reads them, and never answers a count that ignored them.
    /// </summary>
    public static async Task CountFilterCrossesRelations(ShapeStore store)
    {
        var a = "a-" + Guid.NewGuid().ToString("N");
        var b = "b-" + Guid.NewGuid().ToString("N");
        var onA = new ShapeRunner { Name = a };
        var onB = new ShapeRunner { Name = b };
        var build = new ShapeRun { Name = "build" };
        var idle = new ShapeRun { Name = "idle" };
        var deploy = new ShapeRun { Name = "deploy" };
        var logged = new ShapeStep { ShapeRunId = build.Id, Status = "ok", StartedAt = At(1), RunnerId = onA.Id };
        var silent = new ShapeStep { ShapeRunId = build.Id, Status = "ok", StartedAt = At(2), RunnerId = onB.Id };
        var unassigned = new ShapeStep { ShapeRunId = build.Id, Status = "ok", StartedAt = At(3) };
        var shipped = new ShapeStep { ShapeRunId = deploy.Id, Status = "ok", StartedAt = At(4), RunnerId = onA.Id };
        await store.Add(onA, onB);
        await store.Add(build, idle, deploy);
        await store.Add(logged, silent, unassigned, shipped);
        await store.Add(
            new ShapeLog { ShapeStepId = logged.Id, Text = "1" },
            new ShapeLog { ShapeStepId = logged.Id, Text = "2" },
            new ShapeLog { ShapeStepId = unassigned.Id, Text = "3" });
        await store.Commit();

        var ids = new[] { build.Id, idle.Id, deploy.Id };
        var rows = (await store.Repo<ShapeRun>().GetMappedAsync(x => new
            {
                x.Id,
                Busy = x.Steps!.Count(s => s.Logs!.Any()),
                Quiet = x.Steps!.Count(s => !s.Logs!.Any()),
                OnA = x.Steps!.Count(s => s.Runner!.Name == a),
                Unassigned = x.Steps!.LongCount(s => s.Runner == null),
                Either = x.Steps!.Count(s => s.Logs!.Any(l => l.Text == "3") || s.Runner!.Name == b),
            }, new QueryOptions<ShapeRun>().Where(x => ids.Contains(x.Id))))
            .ToDictionary(x => x.Id);

        Assert.That((rows[build.Id].Busy, rows[build.Id].Quiet, rows[build.Id].OnA, rows[build.Id].Unassigned, rows[build.Id].Either),
            Is.EqualTo((2, 1, 1, 1L, 2)), "the steps with logs, without, on runner a, on none, and with log 3 or on runner b");
        Assert.That((rows[idle.Id].Busy, rows[idle.Id].Quiet, rows[idle.Id].OnA, rows[idle.Id].Unassigned, rows[idle.Id].Either),
            Is.EqualTo((0, 0, 0, 0L, 0)), "a run without steps counts zero");
        Assert.That((rows[deploy.Id].Busy, rows[deploy.Id].Quiet, rows[deploy.Id].OnA, rows[deploy.Id].Unassigned, rows[deploy.Id].Either),
            Is.EqualTo((0, 1, 1, 0L, 0)));
    }

    /// <summary>Positions 2, null and 1 come back with the null first or last, ascending and descending.</summary>
    public static async Task NullsFirstOrLast(ShapeStore store)
    {
        var board = "board-" + Guid.NewGuid().ToString("N");
        await store.Add(
            new ShapeCard { Board = board, Position = 2, Rank = 1 },
            new ShapeCard { Board = board, Position = null, Rank = 2 },
            new ShapeCard { Board = board, Position = 1, Rank = 3 });
        await store.Commit();

        async Task<int?[]> Read(Func<QueryOptions<ShapeCard>, QueryOptions<ShapeCard>> order) =>
            (await store.Repo<ShapeCard>().GetAllAsync(order(new QueryOptions<ShapeCard>().Where(x => x.Board == board))))
            .Select(x => x.Position).ToArray();

        Assert.That(await Read(o => o.OrderBy(x => x.Position, NullsOrder.First)), Is.EqualTo(new int?[] { null, 1, 2 }));
        Assert.That(await Read(o => o.OrderBy(x => x.Position, NullsOrder.Last)), Is.EqualTo(new int?[] { 1, 2, null }));
        Assert.That(await Read(o => o.OrderByDescending(x => x.Position, NullsOrder.First)), Is.EqualTo(new int?[] { null, 2, 1 }));
        Assert.That(await Read(o => o.OrderByDescending(x => x.Position, NullsOrder.Last)), Is.EqualTo(new int?[] { 2, 1, null }));
    }

    /// <summary>Rows 11 to 20 of the order come back, and the database runs no count.</summary>
    public static async Task TakeTenWithoutTotal(ShapeStore store)
    {
        var board = "page-" + Guid.NewGuid().ToString("N");
        foreach (var rank in Enumerable.Range(1, 25).OrderBy(rank => (rank * 7) % 25))
        {
            await store.Add(new ShapeCard { Board = board, Rank = rank });
        }

        await store.Commit();
        store.Commands.Clear();

        var page = await store.Repo<ShapeCard>().GetAllAsync(new QueryOptions<ShapeCard>()
            .Where(x => x.Board == board).OrderBy(x => x.Rank).Skip(10).Take(10));
        var mapped = await store.Repo<ShapeCard>().GetMappedAsync(x => x.Rank, new QueryOptions<ShapeCard>()
            .Where(x => x.Board == board).OrderByDescending(x => x.Rank).Skip(20).Take(10));

        Assert.That(page.Select(x => x.Rank), Is.EqualTo(Enumerable.Range(11, 10)), "rows 11 to 20 of the order");
        Assert.That(mapped, Is.EqualTo(new[] { 5, 4, 3, 2, 1 }), "a page past the end holds what is left");
        Assert.That(store.Commands.Statements, Is.Not.Empty, "the reads were captured");
        Assert.That(store.Commands.Statements.Where(statement => statement.Contains("COUNT(", StringComparison.OrdinalIgnoreCase)),
            Is.Empty, "no count ran");

        Assert.That(async () => await store.Repo<ShapeCard>().CountAsync(new QueryOptions<ShapeCard>().Take(10)),
            Throws.InstanceOf<NotSupportedException>(), "a count refuses a window instead of counting it");
    }

    /// <summary>(repo-a, 1) twice and (repo-a, 2) once come back as two pairs.</summary>
    public static async Task DistinctOfTwoMembers(ShapeStore store)
    {
        var repository = "repo-" + Guid.NewGuid().ToString("N");
        await store.Add(
            new ShapeComment { Repository = repository, IssueNumber = 1, Body = "first" },
            new ShapeComment { Repository = repository, IssueNumber = 1, Body = "again" },
            new ShapeComment { Repository = repository, IssueNumber = 2, Body = "other issue" },
            new ShapeComment { Repository = "elsewhere", IssueNumber = 1, Body = "not matched" });
        await store.Commit();

        var distinct = (IDistinctReadRepository<ShapeComment>)store.Repo<ShapeComment>();
        var pairs = await distinct.GetDistinctAsync(x => new { x.Repository, x.IssueNumber }, x => x.Repository == repository);
        Assert.That(pairs.Select(pair => (pair.Repository, pair.IssueNumber)),
            Is.EquivalentTo(new[] { (repository, 1), (repository, 2) }), "each combination once");

        var issues = await distinct.GetDistinctAsync(x => x.IssueNumber, x => x.Repository == repository);
        Assert.That(issues, Is.EquivalentTo(new[] { 1, 2 }), "each value of one member once");

        Assert.That(async () => await distinct.GetDistinctAsync(x => x.IssueNumber, null,
                new QueryOptions<ShapeComment>().OrderBy(x => x.IssueNumber)),
            Throws.InstanceOf<NotSupportedException>(), "distinct values come in no order, and an order is refused, not ignored");
    }

    /// <summary>
    ///     A skip with no limit returns every row after it, of an entity read, a filtered read, a mapped read and a read
    ///     with relation counts; a limit with no skip returns the first rows.
    /// </summary>
    public static async Task SkipWithoutTakeAndTakeWithoutSkip(ShapeStore store)
    {
        var board = "skip-" + Guid.NewGuid().ToString("N");
        foreach (var rank in Enumerable.Range(1, 25).OrderBy(rank => (rank * 7) % 25))
        {
            await store.Add(new ShapeCard { Board = board, Rank = rank });
        }

        var packages = Enumerable.Range(1, 3).Select(index => new ShapePackage { Name = "package-" + index }).ToList();
        await store.Add(packages.ToArray());
        foreach (var (package, index) in packages.Select((package, index) => (package, index)))
        {
            await store.Add(Enumerable.Range(0, index + 1)
                .Select(version => new ShapeVersion { ShapePackageId = package.Id, Label = $"{index}.{version}" }).ToArray());
        }

        await store.Commit();

        var cards = store.Repo<ShapeCard>();
        var skipped = await cards.GetAllAsync(new QueryOptions<ShapeCard>().Where(x => x.Board == board).OrderBy(x => x.Rank).Skip(20));
        Assert.That(skipped.Select(x => x.Rank), Is.EqualTo(Enumerable.Range(21, 5)), "every row after the skip");

        var filtered = await cards.GetFilteredAsync(x => x.Board == board,
            new QueryOptions<ShapeCard>().OrderByDescending(x => x.Rank).Skip(22));
        Assert.That(filtered.Select(x => x.Rank), Is.EqualTo(new[] { 3, 2, 1 }), "a filtered read skips too");

        var mapped = await cards.GetMappedAsync(x => x.Rank,
            new QueryOptions<ShapeCard>().Where(x => x.Board == board).OrderBy(x => x.Rank).Skip(23));
        Assert.That(mapped, Is.EqualTo(new[] { 24, 25 }), "a mapped read skips too");

        var unordered = await cards.GetAllAsync(new QueryOptions<ShapeCard>().Where(x => x.Board == board).Skip(24));
        Assert.That(unordered.Count(), Is.EqualTo(1), "a skip without an order still skips: the store orders by the key");

        var first = await cards.GetAllAsync(new QueryOptions<ShapeCard>().Where(x => x.Board == board).OrderBy(x => x.Rank).Take(3));
        Assert.That(first.Select(x => x.Rank), Is.EqualTo(new[] { 1, 2, 3 }), "a limit without a skip takes the first rows");

        var firstMapped = await cards.GetMappedAsync(x => x.Rank,
            new QueryOptions<ShapeCard>().Where(x => x.Board == board).OrderByDescending(x => x.Rank).Take(2));
        Assert.That(firstMapped, Is.EqualTo(new[] { 25, 24 }));

        var ids = packages.Select(package => package.Id).ToArray();
        var counted = await store.Repo<ShapePackage>().GetMappedAsync(x => new { x.Name, Versions = x.Versions!.Count() },
            new QueryOptions<ShapePackage>().Where(x => ids.Contains(x.Id)).OrderBy(x => x.Name).Skip(1));
        Assert.That(counted.Select(x => (x.Name, x.Versions)), Is.EqualTo(new[] { ("package-2", 2), ("package-3", 3) }),
            "a read with relation counts skips too");

        var countedFirst = await store.Repo<ShapePackage>().GetMappedAsync(x => new { x.Name, Versions = x.Versions!.Count() },
            new QueryOptions<ShapePackage>().Where(x => ids.Contains(x.Id)).OrderBy(x => x.Name).Take(1));
        Assert.That(countedFirst.Select(x => (x.Name, x.Versions)), Is.EqualTo(new[] { ("package-1", 1) }));
    }

    /// <summary>A limit of zero returns nothing, and the store is not asked.</summary>
    public static async Task TakeZero(ShapeStore store)
    {
        var board = "zero-" + Guid.NewGuid().ToString("N");
        var run = new ShapeRun { Name = "zero" };
        await store.Add(new ShapeCard { Board = board, Rank = 1 });
        await store.Add(run);
        await store.Add(Step(run, "success", 1));
        await store.Commit();
        store.Commands.Clear();

        var cards = store.Repo<ShapeCard>();
        Assert.That(await cards.GetAllAsync(new QueryOptions<ShapeCard>().Where(x => x.Board == board).OrderBy(x => x.Rank).Take(0)),
            Is.Empty, "an entity read");
        Assert.That(await cards.GetFilteredAsync(x => x.Board == board, new QueryOptions<ShapeCard>().Skip(1).Take(0)), Is.Empty,
            "a filtered read");
        Assert.That(await cards.GetMappedAsync(x => x.Rank, new QueryOptions<ShapeCard>().Where(x => x.Board == board).Take(0)),
            Is.Empty, "a mapped read");
        Assert.That(await cards.GetFirstAsync(new QueryOptions<ShapeCard>().Where(x => x.Board == board).Take(0)), Is.Null,
            "a first read");
        Assert.That(await store.Repo<ShapePackage>().GetMappedAsync(x => new { x.Name, Versions = x.Versions!.Count() },
            new QueryOptions<ShapePackage>().Take(0)), Is.Empty, "a read with relation counts");
        Assert.That(store.Commands.Statements, Is.Empty, "a limit of zero asks the store nothing");

        var runs = await store.Repo<ShapeRun>().GetAllAsync(new QueryOptions<ShapeRun>()
            .Where(x => x.Id == run.Id)
            .Include(x => x.Steps, steps => steps.OrderBy(s => s.StartedAt).Take(0)));
        Assert.That(runs.Single().Steps, Is.Empty, "an include of no step per run loads none");
        Assert.That(store.Commands.Statements, Has.Count.EqualTo(1), "the runs are read, and an include of none is not");
    }

    /// <summary>
    ///     2,500 runs, more parents than SQL Server binds in one statement: an include loads every run's steps, shaped or
    ///     not, the parents' keys spread over as many statements as the store's limit needs.
    /// </summary>
    public static async Task IncludesPastTheParameterLimit(ShapeStore store)
    {
        var marker = "many-" + Guid.NewGuid().ToString("N");
        foreach (var run in Enumerable.Range(0, 2500).Select(_ => new ShapeRun { Name = marker }))
        {
            await store.Add(run);
            await store.Add(Step(run, "success", 1), Step(run, "failure", 2));
        }

        await store.Commit();

        var plain = (await store.Repo<ShapeRun>().GetAllAsync(new QueryOptions<ShapeRun>()
            .Where(x => x.Name == marker)
            .Include(x => x.Steps))).ToList();
        Assert.That(plain, Has.Count.EqualTo(2500));
        Assert.That(plain.Select(x => x.Steps!.Count), Is.All.EqualTo(2), "every run has both its steps");

        var shaped = (await store.Repo<ShapeRun>().GetAllAsync(new QueryOptions<ShapeRun>()
            .Where(x => x.Name == marker)
            .Include(x => x.Steps, steps => steps.Where(s => s.Status != "skipped").OrderByDescending(s => s.StartedAt).Take(1)))).ToList();
        Assert.That(shaped, Has.Count.EqualTo(2500));
        Assert.That(shaped.Select(x => x.Steps!.Single().StartedAt), Is.All.EqualTo(At(2)), "every run has its newest step");
    }

    private static ShapeStep Step(ShapeRun run, string status, int minute) =>
        new() { ShapeRunId = run.Id, Status = status, StartedAt = At(minute) };

    private static DateTime At(int minute) => T0.AddMinutes(minute);
}
