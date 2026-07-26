using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.MongoDb.Migration;
using MongoDB.Bson;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>
///     Renaming and dropping a collection. MongoDB is the only document store that can rename one — Cosmos fixes a
///     container's name at creation — and a collection has no declared size, so resizing is a no-op rather than an
///     error: the operation is meaningful in the model, and doing nothing is the honest answer here.
/// </summary>
[TestFixture]
public sealed class MongoCollectionOperationsTests : MongoIntegrationTest
{
    private sealed class Ledger
    {
        public string Id { get; set; } = "";
        public string Channel { get; set; } = "";
    }

    private static async Task ApplyAsync(MongoTestDatabase db, Action<IMigrationBuilder> configure)
    {
        var builder = new MigrationBuilder();
        configure(builder);
        await new MongoMigrationExecutor(db.Database).ApplyAsync(builder.Operations);
    }

    private static async Task<bool> ExistsAsync(MongoTestDatabase db, string name)
    {
        var options = new ListCollectionNamesOptions { Filter = Builders<BsonDocument>.Filter.Eq("name", name) };
        using var cursor = await db.Database.ListCollectionNamesAsync(options);
        return await cursor.AnyAsync();
    }

    [Test]
    public async Task Renaming_a_collection_moves_it_and_its_documents()
    {
        using var db = await Task.FromResult(NewDatabase());
        await db.Database.GetCollection<BsonDocument>("ledgers")
            .InsertOneAsync(new BsonDocument { ["_id"] = "a", ["Channel"] = "web" });

        await ApplyAsync(db, migration => migration.For<Ledger>(l => l.RenameCollection("ledgers", "ledgers_v2")));

        Assert.That(await ExistsAsync(db, "ledgers_v2"), Is.True);
        Assert.That(await ExistsAsync(db, "ledgers"), Is.False, "a rename moves it, it does not copy it");
        var moved = await db.Database.GetCollection<BsonDocument>("ledgers_v2")
            .Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.That(moved["Channel"].AsString, Is.EqualTo("web"), "the documents travel with the collection");
    }

    [Test]
    public async Task Dropping_a_collection_removes_it()
    {
        using var db = await Task.FromResult(NewDatabase());
        await db.Database.GetCollection<BsonDocument>("ledgers")
            .InsertOneAsync(new BsonDocument { ["_id"] = "a" });
        Assert.That(await ExistsAsync(db, "ledgers"), Is.True);

        await ApplyAsync(db, migration => migration.For<Ledger>(l => l.DropCollection("ledgers")));

        Assert.That(await ExistsAsync(db, "ledgers"), Is.False);
    }

    [Test]
    public async Task Resizing_a_field_does_nothing_rather_than_failing()
    {
        using var db = await Task.FromResult(NewDatabase());
        await db.Database.GetCollection<BsonDocument>("ledgers")
            .InsertOneAsync(new BsonDocument { ["_id"] = "a", ["Channel"] = "web" });

        await ApplyAsync(db, migration => migration.For<Ledger>(l => l.ResizeField(x => x.Channel)));

        var document = await db.Database.GetCollection<BsonDocument>("ledgers")
            .Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.That(document["Channel"].AsString, Is.EqualTo("web"),
            "a document's field is as big as its value; a migration written once for six stores must not throw here");
    }
}
