using eQuantic.Core.Data.MongoDb.Extensions;
using eQuantic.Core.Data.MongoDb.Repository;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>A unit of work that keeps every entity in a collection of its own naming: the tenant's prefix, then the type's name.</summary>
public sealed class TenantPrefixedUnitOfWork(IServiceProvider serviceProvider, IMongoClient client, IMongoDatabase database)
    : MongoUnitOfWork(serviceProvider, client, database)
{
    public const string Prefix = "tenant_a.";

    protected override string CollectionName<TEntity>() => Prefix + base.CollectionName<TEntity>();
}

/// <summary>A note the store numbers (a sequence, by annotation), and nothing else: no enum whose serializer it would fix.</summary>
public sealed class NumberedNote : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Text { get; set; } = "";

    [Modeling.StoreGenerated]
    public long Number { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>
///     A unit of work that overrides <c>CollectionName&lt;TEntity&gt;()</c>, against a real MongoDB: every read that joins
///     another collection — a plain include, a shaped include, a relation count, a filter across a relation — reads the
///     related documents from the collection the unit of work keeps them in, never from the one the type would name.
/// </summary>
[TestFixture]
public sealed class MongoCollectionNameOverrideTests : MongoIntegrationTest
{
    private ServiceProvider _provider = null!;
    private IMongoDatabase _database = null!;

    [SetUp]
    public void Register()
    {
        _database = MongoTestServer.Client.GetDatabase("test_" + Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection();
        services.AddSingleton(MongoTestServer.Client);
        services.AddSingleton(_database);
        services.AddMongoRepositories<TenantPrefixedUnitOfWork>();
        _provider = services.BuildServiceProvider();
    }

    [TearDown]
    public void Dispose() => _provider?.Dispose();

    private static IAsyncRepository<TEntity, string> Repo<TEntity>(IServiceScope scope) where TEntity : class, IEntity<string> =>
        scope.ServiceProvider.GetRequiredService<IAsyncRepository<TEntity, string>>();

    private async Task Seed(params object[] entities)
    {
        using var scope = _provider.CreateScope();
        foreach (var entity in entities)
        {
            var repository = scope.ServiceProvider.GetRequiredService(typeof(IAsyncRepository<,>).MakeGenericType(entity.GetType(), typeof(string)));
            await (Task)repository.GetType().GetMethod(nameof(IAsyncRepository<ShapeRun, string>.AddAsync))!.Invoke(repository, [entity, CancellationToken.None])!;
        }

        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync();
    }

    [Test]
    public async Task The_documents_live_in_the_overridden_collections()
    {
        await Seed(new ShapeRun { Name = "build" });

        var names = await (await _database.ListCollectionNamesAsync()).ToListAsync();
        Assert.That(names, Does.Contain(TenantPrefixedUnitOfWork.Prefix + nameof(ShapeRun)));
        Assert.That(names, Does.Not.Contain(nameof(ShapeRun)));
    }

    [Test]
    public async Task A_shaped_include_joins_the_overridden_collection()
    {
        var run = new ShapeRun { Name = "build" };
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await Seed(run,
            new ShapeStep { ShapeRunId = run.Id, Status = "done", StartedAt = t0.AddMinutes(1) },
            new ShapeStep { ShapeRunId = run.Id, Status = "failed", StartedAt = t0.AddMinutes(2) },
            new ShapeStep { ShapeRunId = run.Id, Status = "done", StartedAt = t0.AddMinutes(3) });

        using var scope = _provider.CreateScope();
        var shaped = (await Repo<ShapeRun>(scope).GetAllAsync(new QueryOptions<ShapeRun>()
            .Include(x => x.Steps, steps => steps.Where(step => step.Status == "done").OrderByDescending(step => step.StartedAt))))
            .Single();
        Assert.That(shaped.Steps!.Select(step => step.StartedAt), Is.EqualTo(new[] { t0.AddMinutes(3), t0.AddMinutes(1) }),
            "the shaped $lookup read the steps from the unit of work's collection");

        var plain = (await Repo<ShapeRun>(scope).GetAllAsync(new QueryOptions<ShapeRun>().Include(x => x.Steps))).Single();
        Assert.That(plain.Steps, Has.Count.EqualTo(3), "as the plain include does");
    }

    [Test]
    public async Task A_relation_count_counts_in_the_overridden_collection()
    {
        var package = new ShapePackage { Name = "core" };
        await Seed(package,
            new ShapeVersion { ShapePackageId = package.Id, Label = "1.0.0" },
            new ShapeVersion { ShapePackageId = package.Id, Label = "1.1.0", DeletedAt = DateTime.UtcNow },
            new ShapeVersion { ShapePackageId = package.Id, Label = "2.0.0" });

        using var scope = _provider.CreateScope();
        var counted = (await Repo<ShapePackage>(scope).GetMappedAsync(x => new
        {
            x.Name,
            Live = x.Versions!.Count(v => v.DeletedAt == null),
            All = x.Versions!.Count(),
        })).Single();
        Assert.That((counted.Live, counted.All), Is.EqualTo((2, 3)), "the counting $lookup read the versions from the unit of work's collection");

        Assert.That((await Repo<ShapePackage>(scope).GetFilteredAsync(x => x.Versions!.Any(v => v.Label == "2.0.0"))).Single().Id,
            Is.EqualTo(package.Id), "as a filter across the relation does");
    }

    [Test]
    public async Task A_counter_is_named_after_the_collection_the_unit_of_work_keeps_the_entity_in()
    {
        await Seed(new NumberedNote { Text = "a" }, new NumberedNote { Text = "b" });

        var counters = await _database.GetCollection<BsonDocument>(MongoSequences.CollectionName).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        Assert.That(counters.Select(counter => (counter["_id"].AsString, counter["value"].ToInt64())),
            Is.EqualTo(new[] { (TenantPrefixedUnitOfWork.Prefix + nameof(NumberedNote) + ".Number", 2L) }),
            "the counter of the collection the unit of work writes, never the one the type would name");

        // A unit of work that keeps the entity under the type's own name numbers its collection with a counter of its own.
        using var plain = new MongoTestDatabase(MongoTestServer.Client, _database.DatabaseNamespace.DatabaseName, []);
        var note = new NumberedNote { Text = "c" };
        await plain.Resolve<IAsyncRepository<NumberedNote, string>>().AddAsync(note);
        await plain.Resolve<IUnitOfWork>().CommitAsync();
        Assert.That(note.Number, Is.EqualTo(1L), "another collection, another counter");
    }

    [Test]
    public async Task A_new_counter_starts_above_the_numbers_its_collection_already_holds()
    {
        // Numbers a counter of another name handed out before this one took the collection's name.
        await _database.GetCollection<BsonDocument>(TenantPrefixedUnitOfWork.Prefix + nameof(NumberedNote))
            .InsertOneAsync(new BsonDocument { { "_id", Guid.NewGuid().ToString("N") }, { "Text", "old" }, { "Number", 41L } });

        var note = new NumberedNote { Text = "new" };
        await Seed(note);

        Assert.That(note.Number, Is.EqualTo(42L), "no number is handed out twice");
    }
}
