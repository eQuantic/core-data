using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.MongoDb.Migration;
using eQuantic.Core.Data.Repository;
using MongoDB.Bson;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>A versioned document — <c>[ConcurrencyToken]</c> makes every replace conditional on the read version.</summary>
[Entity("versioned_docs")]
public sealed class VersionedDoc : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Owner { get; set; } = "";

    public decimal Balance { get; set; }

    [ConcurrencyToken]
    public long Version { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>
///     An expiring document: <c>[TimeToLive]</c> plus the lifecycle <c>CreatedAt</c> (<c>IEntityTimeMark</c>)
///     gives the TTL index its date member by convention.
/// </summary>
[Entity("expiring_docs")]
[TimeToLive(3600)]
public sealed class ExpiringDoc : IEntity<string>, eQuantic.Core.Domain.Entities.IEntityTimeMark
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Body { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>
///     Proves optimistic concurrency and per-document TTL against a real MongoDB: a stale writer's commit
///     throws instead of silently overwriting, and <c>EnsureCollection()</c> materializes the TTL index.
/// </summary>
[TestFixture]
public sealed class MongoConcurrencyAndTtlTests : MongoIntegrationTest
{
    [Test]
    public async Task First_commit_writes_version_one_and_a_stale_writer_loses()
    {
        using var db = NewDatabase();
        var repo = db.Resolve<IAsyncRepository<VersionedDoc, string>>();

        var doc = new VersionedDoc { Owner = "ana", Balance = 100m };
        await repo.AddAsync(doc);
        await Uow(db).CommitAsync();
        Assert.That(doc.Version, Is.EqualTo(1), "the first persisted version is 1");

        var first = await repo.GetAsync(doc.Id);
        var second = await repo.GetAsync(doc.Id);

        first!.Balance = 150m;
        await repo.ModifyAsync(first);
        await Uow(db).CommitAsync();
        Assert.That(first.Version, Is.EqualTo(2), "the winning write bumped the version");

        second!.Balance = 999m;
        await repo.ModifyAsync(second);
        Assert.ThrowsAsync<ConcurrencyConflictException>(() => Uow(db).CommitAsync(),
            "the stale replace filters on version 1, which no longer matches");

        var current = await repo.GetAsync(doc.Id);
        Assert.That(current!.Balance, Is.EqualTo(150m), "the winning write survived; the stale one changed nothing");
    }

    [Test]
    public async Task Deleting_a_stale_read_is_a_conflict()
    {
        using var db = NewDatabase();
        var repo = db.Resolve<IAsyncRepository<VersionedDoc, string>>();

        var doc = new VersionedDoc { Owner = "ana" };
        await repo.AddAsync(doc);
        await Uow(db).CommitAsync();

        var stale = await repo.GetAsync(doc.Id);
        var winner = await repo.GetAsync(doc.Id);
        winner!.Balance = 5m;
        await repo.ModifyAsync(winner);
        await Uow(db).CommitAsync();

        await repo.RemoveAsync(stale!);
        Assert.ThrowsAsync<ConcurrencyConflictException>(() => Uow(db).CommitAsync(),
            "the delete filters on the read version, which another writer moved past");
    }

    [Test]
    public async Task EnsureCollection_materializes_the_ttl_index_from_the_annotation()
    {
        using var db = NewDatabase();

        var builder = new MigrationBuilder();
        builder.For<ExpiringDoc>(doc => doc.EnsureCollection());
        await new MongoMigrationExecutor(db.Database).ApplyAsync(builder.Operations);

        var indexes = await (await db.Database.GetCollection<BsonDocument>("expiring_docs").Indexes.ListAsync()).ToListAsync();
        var ttl = indexes.SingleOrDefault(index => index.GetValue("name", "").AsString == "ttl_CreatedAt");
        Assert.That(ttl, Is.Not.Null, "[TimeToLive] + IEntityTimeMark yields a TTL index on CreatedAt");
        Assert.That(ttl!["expireAfterSeconds"].ToDouble(), Is.EqualTo(3600));
    }

    [Test]
    public async Task EnsureCollection_materializes_the_clustering_compound_index()
    {
        using var db = NewDatabase();

        var builder = new MigrationBuilder();
        builder.For<RankedDoc>(doc => doc.EnsureCollection());
        await new MongoMigrationExecutor(db.Database).ApplyAsync(builder.Operations);

        var indexes = await (await db.Database.GetCollection<BsonDocument>("ranked_docs").Indexes.ListAsync()).ToListAsync();
        var clustering = indexes.SingleOrDefault(index => index.GetValue("name", "").AsString == "ix_clustering");
        Assert.That(clustering, Is.Not.Null, "[ClusteringKey] materialized the ordered-read compound index");
        var key = clustering!["key"].AsBsonDocument;
        Assert.That(key["Category"].ToInt32(), Is.EqualTo(1), "the first member ascends");
        Assert.That(key["Score"].ToInt32(), Is.EqualTo(-1), "the second member descends, as declared");
    }
}

/// <summary>A ranked document — <c>[ClusteringKey]</c> declares the ordered read the compound index serves.</summary>
[Entity("ranked_docs")]
public sealed class RankedDoc : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [ClusteringKey(Order = 0)]
    public string Category { get; set; } = "";

    [ClusteringKey(Order = 1, Descending = true)]
    public int Score { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}
