using System.Collections.Concurrent;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Read;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace eQuantic.Core.Data.PostgreSql.Tests;

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

    private static ShapeStep Step(ShapeRun run, string status, int minute) =>
        new() { ShapeRunId = run.Id, Status = status, StartedAt = At(minute) };

    private static DateTime At(int minute) => T0.AddMinutes(minute);
}
