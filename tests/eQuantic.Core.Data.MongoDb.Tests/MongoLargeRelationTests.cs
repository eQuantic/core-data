using System.Collections.Concurrent;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Write;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>An owner whose key is long text, so that its keys outgrow what one <c>$in</c> can carry.</summary>
public sealed class WideOwner : IEntity<string>
{
    public string Id { get; set; } = "";

    public string Group { get; set; } = "";

    public List<WideChild>? Children { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>A child holding the foreign key back to its owner by convention (<c>WideOwnerId</c>).</summary>
public sealed class WideChild : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string WideOwnerId { get; set; } = "";

    public int Rank { get; set; }

    [BsonIgnoreIfNull]
    public WideOwner? WideOwner { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>
///     Filters across a relation whose related keys are more than one <c>$in</c> can carry (past 16 MB, which MongoDB
///     refuses outright), against a real MongoDB: reads, counts and the synchronous read join the related documents with a
///     <c>$lookup</c> in their own aggregation, and set-based writes run over the matching documents' ids in batches. The
///     keys an asynchronous read resolves first are read asynchronously, and its token stops them.
/// </summary>
[TestFixture]
public sealed class MongoLargeRelationTests : MongoIntegrationTest
{
    private const int Owners = 9_000;
    private static readonly string KeyPadding = new('k', 2_000);

    private MongoTestDatabase _db = null!;

    [OneTimeSetUp]
    public async Task SeedOnce()
    {
        MongoTestServer.EnsureAvailable();
        _db = NewDatabase();
        var owners = _db.Database.GetCollection<WideOwner>(nameof(WideOwner));
        var children = _db.Database.GetCollection<WideChild>(nameof(WideChild));
        await children.Indexes.CreateOneAsync(new CreateIndexModel<WideChild>(Builders<WideChild>.IndexKeys.Ascending(x => x.WideOwnerId)));

        for (var start = 0; start < Owners; start += 1_000)
        {
            var batch = Enumerable.Range(start, Math.Min(1_000, Owners - start)).Select(index => new WideOwner { Id = Key(index), Group = "many" }).ToList();
            await owners.InsertManyAsync(batch);
            await children.InsertManyAsync(batch.Select(owner => new WideChild { WideOwnerId = owner.Id, Rank = 1 }));
        }

        await owners.InsertOneAsync(new WideOwner { Id = "lonely", Group = "alone" });
    }

    [OneTimeTearDown]
    public void DropDatabase() => _db?.Dispose();

    private static string Key(int index) => KeyPadding + index.ToString("D6");

    private IAsyncRepository<WideOwner, string> Repo(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IAsyncRepository<WideOwner, string>>();

    [Test]
    public async Task An_asynchronous_read_joins_the_related_documents_past_what_an_in_carries()
    {
        using var scope = _db.NewScope();
        var related = await Repo(scope).GetFilteredAsync(x => x.Children!.Any(child => child.Rank > 0));
        Assert.That(related.Count(), Is.EqualTo(Owners));

        Assert.That(await Repo(scope).CountAsync(new QueryOptions<WideOwner>().Where(x => !x.Children!.Any())), Is.EqualTo(1L),
            "the negated test, counted");
        Assert.That((await Repo(scope).GetFilteredAsync(x => x.Group == "alone" || x.Children!.All(child => child.Rank > 5))).Single().Id,
            Is.EqualTo("lonely"), "inside OR, and All as no child failing it");
        Assert.That(await Repo(scope).CountAsync(new QueryOptions<WideOwner>().Where(x => x.Children!.Any(child => child.Rank > 5))),
            Is.Zero, "a condition no related document meets");
        Assert.That(await Repo(scope).CountAsync(new QueryOptions<WideOwner>().Where(x => x.Children!.Any(child => child.WideOwner!.Group == "many"))),
            Is.EqualTo((long)Owners), "a condition crossing a relation in turn, past the bound too: a $lookup inside the $lookup");
    }

    [Test]
    public void A_synchronous_read_joins_the_related_documents_past_what_an_in_carries()
    {
        using var scope = _db.NewScope();
        var repository = (IRepository<WideOwner, string>)Repo(scope);
        Assert.That(repository.GetFiltered(x => x.Children!.Any()).Count(), Is.EqualTo(Owners));
        Assert.That(repository.Count(new QueryOptions<WideOwner>().Where(x => !x.Children!.Any())), Is.EqualTo(1L));
    }

    [Test]
    public async Task A_set_based_write_runs_over_the_matching_ids_past_what_an_in_carries()
    {
        using var scope = _db.NewScope();
        var repo = Repo(scope);
        try
        {
            Assert.That(await repo.UpdateManyAsync(x => x.Children!.Any(child => child.Rank > 0) && x.Group == "many",
                x => new WideOwner { Group = "touched" }), Is.EqualTo((long)Owners));
            Assert.That(await repo.CountAsync(new QueryOptions<WideOwner>().Where(x => x.Group == "touched")), Is.EqualTo((long)Owners));

            Assert.That(await repo.UpdateManyAsync(x => x.Group == "alone" || x.Children!.Any(child => child.Rank > 5),
                x => new WideOwner { Group = "alone, again" }), Is.EqualTo(1L), "the rest of the filter holds for the documents left out");

            var returned = await ((IReturningUpdateRepository<WideOwner, string>)repo).UpdateAndGetAsync(
                x => x.Children!.Any() && x.Id == Key(7), x => new WideOwner { Group = "returned" });
            Assert.That(returned.Select(owner => owner.Id), Is.EqualTo(new[] { Key(7) }));
        }
        finally
        {
            await repo.UpdateManyAsync(x => x.Id != "lonely", x => new WideOwner { Group = "many" });
            await repo.UpdateManyAsync(x => x.Id == "lonely", x => new WideOwner { Group = "alone" });
        }
    }

    [Test]
    public async Task A_set_based_delete_runs_over_the_matching_ids_past_what_an_in_carries()
    {
        using var scope = _db.NewScope();
        var repo = Repo(scope);
        await repo.AddAsync(new WideOwner { Id = "doomed", Group = "alone" });
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync();

        Assert.That(await repo.DeleteManyAsync(x => !x.Children!.Any() && x.Id == "doomed"), Is.EqualTo(1L));
        Assert.That(await repo.CountAsync(new QueryOptions<WideOwner>().Where(x => !x.Children!.Any())), Is.EqualTo(1L),
            "only the document named went");
    }

    [Test]
    public async Task An_asynchronous_read_resolves_its_relations_with_its_token()
    {
        var sent = new ConcurrentQueue<BsonDocument>();
        var settings = MongoTestServer.Client.Settings.Clone();
        settings.ClusterConfigurator = cluster =>
            cluster.Subscribe<CommandStartedEvent>(started => sent.Enqueue(started.Command.DeepClone().AsBsonDocument));
        using var client = new MongoClient(settings);
        using var db = new MongoTestDatabase(client, _db.Database.DatabaseNamespace.DatabaseName, [], null);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.That(async () => await db.Resolve<IAsyncRepository<WideOwner, string>>()
                .GetFilteredAsync(x => x.Children!.Any(child => child.Rank > 0), cancellationToken: cancelled.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(sent.Where(command => command.Contains("aggregate")).Select(command => command["aggregate"].ToString()),
            Is.Empty, "the related keys were never read: the cancelled token stopped the read before it was sent");
    }
}
