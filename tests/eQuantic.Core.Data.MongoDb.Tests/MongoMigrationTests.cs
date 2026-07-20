using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.MongoDb.Migration;
using MongoDB.Bson;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb.Tests;

[TestFixture]
public sealed class MongoMigrationTests : MongoIntegrationTest
{
    [Test]
    public async Task Runner_applies_pending_once_and_skips_on_the_second_run()
    {
        using var db = MongoTestServer.NewDatabase(typeof(ProductsSetupMigration).Assembly);
        var runner = db.Resolve<IMigrationRunner>();

        Assert.That(await runner.RunAsync(), Is.EqualTo(1));
        Assert.That(await runner.RunAsync(), Is.EqualTo(0));

        var recorded = await db.Database.GetCollection<BsonDocument>("_migrations")
            .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        Assert.That(recorded, Has.Count.EqualTo(1));
        Assert.That(recorded[0]["_id"].AsString, Does.Contain("Products setup"));
    }

    [Test]
    public async Task Runner_creates_the_declared_indexes()
    {
        using var db = MongoTestServer.NewDatabase(typeof(ProductsSetupMigration).Assembly);
        await db.Resolve<IMigrationRunner>().RunAsync();

        var indexes = await (await db.Database.GetCollection<Product>("Product").Indexes.ListAsync()).ToListAsync();
        var keys = indexes.Select(index => index["key"].AsBsonDocument).ToList();

        Assert.That(keys.Any(key => key.Contains("Category")), Is.True);
        Assert.That(keys.Any(key => key.Contains("Price") && key.Contains("Name")), Is.True);
    }

    [Test]
    public async Task ConvertField_changes_the_stored_type()
    {
        using var db = MongoTestServer.NewDatabase();
        var raw = db.Database.GetCollection<BsonDocument>("Product");
        await raw.InsertOneAsync(new BsonDocument { { "_id", "p1" }, { "Quantity", "5" } });

        var executor = new MongoMigrationExecutor(db.Database);
        var builder = new MigrationBuilder();
        builder.For<Product>(product => product.ConvertField(x => x.Quantity, MigrationFieldType.String, MigrationFieldType.Int32));
        await executor.ApplyAsync(builder.Operations);

        var stored = await raw.Find(Builders<BsonDocument>.Filter.Eq("_id", "p1")).FirstAsync();
        Assert.That(stored["Quantity"].BsonType, Is.EqualTo(BsonType.Int32));
        Assert.That(stored["Quantity"].AsInt32, Is.EqualTo(5));
    }

    [Test]
    public async Task RenameField_renames_across_documents()
    {
        using var db = MongoTestServer.NewDatabase();
        var raw = db.Database.GetCollection<BsonDocument>("Product");
        await raw.InsertOneAsync(new BsonDocument { { "_id", "p1" }, { "Name", "Widget" } });

        var executor = new MongoMigrationExecutor(db.Database);
        var builder = new MigrationBuilder();
        builder.For<Product>(product => product.RenameField(x => x.Name, "DisplayName"));
        await executor.ApplyAsync(builder.Operations);

        var stored = await raw.Find(Builders<BsonDocument>.Filter.Eq("_id", "p1")).FirstAsync();
        Assert.That(stored.Contains("DisplayName"), Is.True);
        Assert.That(stored.Contains("Name"), Is.False);
        Assert.That(stored["DisplayName"].AsString, Is.EqualTo("Widget"));
    }

    [Test]
    public async Task Update_sets_the_field_on_matching_documents()
    {
        using var db = MongoTestServer.NewDatabase();
        var raw = db.Database.GetCollection<BsonDocument>("Product");
        await raw.InsertManyAsync(
        [
            new BsonDocument { { "_id", "p1" }, { "Category", "old" } },
            new BsonDocument { { "_id", "p2" }, { "Category", "old" } },
            new BsonDocument { { "_id", "p3" }, { "Category", "keep" } },
        ]);

        var executor = new MongoMigrationExecutor(db.Database);
        var builder = new MigrationBuilder();
        builder.For<Product>(product => product.Update(
            x => x.Category == "old",
            update => update.Set(x => x.Category, "new")));
        await executor.ApplyAsync(builder.Operations);

        var migrated = await raw.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("Category", "new"));
        Assert.That(migrated, Is.EqualTo(2));
    }
}
