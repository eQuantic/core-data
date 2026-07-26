using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.MongoDb.Migration;
using MongoDB.Bson;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>
///     Adding a mapped field to a collection that already holds documents.
///     <para>
///         The collection needs no declaration to accept the field — documents gain one on write. The documents
///         already there are the problem: absent the field, the driver hands the application <c>default(T)</c>, and
///         a <c>0</c> is indistinguishable from a zero somebody meant. So a declared default has to reach the old
///         documents, and an undeclared one must not invent a value to reach them with.
///     </para>
/// </summary>
[TestFixture]
public sealed class MongoAddFieldBackfillTests : MongoIntegrationTest
{
    private sealed class Ledger
    {
        public string Id { get; set; } = "";

        [DefaultValue("web")]
        public string Channel { get; set; } = "";

        [DefaultValue(3)]
        public int Tier { get; set; }

        public string? Note { get; set; }
    }

    private static async Task<List<BsonDocument>> SeedAsync(MongoTestDatabase db, params string[] ids)
    {
        var collection = db.Database.GetCollection<BsonDocument>(nameof(Ledger));
        await collection.InsertManyAsync(ids.Select(id => new BsonDocument { ["_id"] = id }));
        return await collection.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
    }

    private static async Task ApplyAsync(MongoTestDatabase db, Action<IMigrationBuilder> configure)
    {
        var builder = new MigrationBuilder();
        configure(builder);
        await new MongoMigrationExecutor(db.Database).ApplyAsync(builder.Operations);
    }

    private static async Task<List<BsonDocument>> ReadAsync(MongoTestDatabase db) =>
        await db.Database.GetCollection<BsonDocument>(nameof(Ledger))
            .Find(FilterDefinition<BsonDocument>.Empty).SortBy(document => document["_id"]).ToListAsync();

    [Test]
    public async Task A_declared_default_reaches_the_documents_that_predate_the_field()
    {
        using var db = NewDatabase();
        await SeedAsync(db, "a", "b");

        await ApplyAsync(db, migration => migration.For<Ledger>(ledger => ledger
            .AddField(x => x.Channel)
            .AddField(x => x.Tier)));

        var documents = await ReadAsync(db);
        Assert.That(documents.Select(d => d["Channel"].AsString), Is.All.EqualTo("web"),
            "every document written before the member existed holds what the model says it holds");
        Assert.That(documents.Select(d => d["Tier"].AsInt32), Is.All.EqualTo(3),
            "the value is serialized as the member's own type, not as a string");
    }

    [Test]
    public async Task A_member_that_declares_nothing_leaves_the_documents_alone()
    {
        using var db = NewDatabase();
        await SeedAsync(db, "a");

        await ApplyAsync(db, migration => migration.For<Ledger>(ledger => ledger.AddField(x => x.Note)));

        var document = (await ReadAsync(db)).Single();
        Assert.That(document.Contains("Note"), Is.False,
            "inventing a value would be worse than the absence: the absence is at least visible");
    }

    [Test]
    public async Task Documents_that_already_carry_the_field_keep_what_they_have()
    {
        using var db = NewDatabase();
        var collection = db.Database.GetCollection<BsonDocument>(nameof(Ledger));
        await collection.InsertOneAsync(new BsonDocument { ["_id"] = "a", ["Channel"] = "phone" });
        await collection.InsertOneAsync(new BsonDocument { ["_id"] = "b" });

        await ApplyAsync(db, migration => migration.For<Ledger>(ledger => ledger.AddField(x => x.Channel)));

        var documents = await ReadAsync(db);
        Assert.That(documents[0]["Channel"].AsString, Is.EqualTo("phone"),
            "a backfill fills what is missing; it does not overwrite what is there");
        Assert.That(documents[1]["Channel"].AsString, Is.EqualTo("web"));
    }

    [Test]
    public async Task Running_it_twice_changes_nothing_the_second_time()
    {
        using var db = NewDatabase();
        await SeedAsync(db, "a");

        await ApplyAsync(db, migration => migration.For<Ledger>(ledger => ledger.AddField(x => x.Channel)));
        var collection = db.Database.GetCollection<BsonDocument>(nameof(Ledger));
        await collection.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", "a"),
            Builders<BsonDocument>.Update.Set("Channel", "phone"));

        await ApplyAsync(db, migration => migration.For<Ledger>(ledger => ledger.AddField(x => x.Channel)));

        Assert.That((await ReadAsync(db)).Single()["Channel"].AsString, Is.EqualTo("phone"),
            "re-running a migration must not undo what happened after it");
    }
}
