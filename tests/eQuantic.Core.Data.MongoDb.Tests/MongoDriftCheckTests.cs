using eQuantic.Core.Data.Evolution;
using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.MongoDb.Evolution;
using eQuantic.Core.Data.MongoDb.Migration;
using MongoDB.Bson;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>
///     Drift on MongoDB, at the only level MongoDB has one. A collection has no schema to compare, so no field is
///     claimed here. What it does have is the indexes the model asked for — and one of them, the time-to-live
///     index, is not about speed: without it nothing expires, and documents that should have been deleted are still
///     being read. That is the finding worth having, and it is the one this reports as a fault.
/// </summary>
[TestFixture]
public sealed class MongoDriftCheckTests : MongoIntegrationTest
{
    private static MongoModel Model()
    {
        var builder = new MongoModelBuilder();
        builder.Entity<ExpiringDoc>(_ => { });
        builder.Entity<RankedDoc>(_ => { });
        return builder.Build();
    }

    private static async Task<DriftReport> CheckAsync(MongoTestDatabase db)
    {
        var source = new MongoDatabaseSnapshotSource(Model(), db.Database);
        return DriftComparer.Compare(source.Expect(), await source.ObserveAsync());
    }

    private static async Task CreateAsync(MongoTestDatabase db)
    {
        var builder = new MigrationBuilder();
        builder.For<ExpiringDoc>(doc => doc.EnsureCollection());
        builder.For<RankedDoc>(doc => doc.EnsureCollection());
        await new MongoMigrationExecutor(db.Database).ApplyAsync(builder.Operations);
    }

    [Test]
    public async Task Collections_the_engine_created_report_nothing()
    {
        using var db = NewDatabase();
        await CreateAsync(db);

        var report = await CheckAsync(db);

        Assert.That(report.Findings, Is.Empty,
            "the indexes the model declares must be the ones EnsureCollection creates, spelled the same way");
    }

    [Test]
    public async Task A_missing_ttl_index_is_a_fault_because_nothing_else_expires_documents()
    {
        using var db = NewDatabase();
        await CreateAsync(db);
        await db.Database.GetCollection<BsonDocument>("expiring_docs").Indexes.DropOneAsync("ttl_CreatedAt");

        var report = await CheckAsync(db);
        var finding = report.Findings.Single();

        Assert.That(finding.Kind, Is.EqualTo(DriftKind.MissingIndex));
        Assert.That(finding.Field, Is.EqualTo("ttl_CreatedAt"));
        Assert.That(report.Breaks, Is.True,
            "an index that expires documents is the one whose absence changes what the store holds");
    }

    [Test]
    public async Task A_missing_ordinary_index_is_reported_without_failing_the_check()
    {
        using var db = NewDatabase();
        await CreateAsync(db);
        await db.Database.GetCollection<BsonDocument>("ranked_docs").Indexes.DropOneAsync("ix_clustering");

        var report = await CheckAsync(db);

        Assert.That(report.Findings.Single().Kind, Is.EqualTo(DriftKind.MissingIndex));
        Assert.That(report.Breaks, Is.False,
            "an ordinary index changes how fast a query runs, not whether it answers");
    }

    [Test]
    public async Task An_index_nobody_declared_is_reported_without_being_treated_as_a_fault()
    {
        using var db = NewDatabase();
        await CreateAsync(db);
        await db.Database.GetCollection<BsonDocument>("ranked_docs").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("Body"),
                new CreateIndexOptions { Name = "somebody_elses" }));

        var report = await CheckAsync(db);

        Assert.That(report.Findings.Single().Kind, Is.EqualTo(DriftKind.UnexpectedIndex));
        Assert.That(report.Breaks, Is.False);
    }

    [Test]
    public async Task An_index_rebuilt_on_different_members_is_found()
    {
        using var db = NewDatabase();
        await CreateAsync(db);
        var collection = db.Database.GetCollection<BsonDocument>("ranked_docs");
        await collection.Indexes.DropOneAsync("ix_clustering");
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("Category"),
            new CreateIndexOptions { Name = "ix_clustering" }));

        var finding = (await CheckAsync(db)).Findings.Single();

        Assert.That(finding.Kind, Is.EqualTo(DriftKind.IndexDiffers));
        Assert.That(finding.Expected, Is.EqualTo("Category:1,Score:-1"));
        Assert.That(finding.Found, Is.EqualTo("Category:1"),
            "the order and direction of an index's keys are part of what it is");
    }

    [Test]
    public async Task No_field_is_ever_claimed_because_a_collection_has_no_schema()
    {
        using var db = NewDatabase();
        await CreateAsync(db);

        var source = new MongoDatabaseSnapshotSource(Model(), db.Database);

        Assert.That(source.Expect().Collections.SelectMany(collection => collection.Fields), Is.Empty);
        Assert.That((await source.ObserveAsync()).Collections.SelectMany(collection => collection.Fields), Is.Empty,
            "sampling documents would describe the ones that came back, not the collection");
    }
}
