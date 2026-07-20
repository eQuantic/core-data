using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.MongoDb.Migration;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace eQuantic.Core.Data.MongoDb.Tests;

[Collection("mongo")]
public sealed class MongoMigrationTests(MongoServerFixture fixture)
{
    [Fact]
    public async Task Runner_applies_pending_once_and_skips_on_the_second_run()
    {
        using var db = fixture.NewDatabase(typeof(ProductsSetupMigration).Assembly);
        var runner = db.Resolve<IMigrationRunner>();

        (await runner.RunAsync()).Should().Be(1);
        (await runner.RunAsync()).Should().Be(0);

        var recorded = await db.Database.GetCollection<BsonDocument>("_migrations")
            .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        recorded.Should().ContainSingle();
        recorded[0]["_id"].AsString.Should().Contain("Products setup");
    }

    [Fact]
    public async Task Runner_creates_the_declared_indexes()
    {
        using var db = fixture.NewDatabase(typeof(ProductsSetupMigration).Assembly);
        await db.Resolve<IMigrationRunner>().RunAsync();

        var indexes = await (await db.Database.GetCollection<Product>("Product").Indexes.ListAsync()).ToListAsync();
        var keys = indexes.Select(index => index["key"].AsBsonDocument).ToList();

        keys.Should().Contain(key => key.Contains("Category"));
        keys.Should().Contain(key => key.Contains("Price") && key.Contains("Name"));
    }

    [Fact]
    public async Task ConvertField_changes_the_stored_type()
    {
        using var db = fixture.NewDatabase();
        var raw = db.Database.GetCollection<BsonDocument>("Product");
        await raw.InsertOneAsync(new BsonDocument { { "_id", "p1" }, { "Quantity", "5" } });

        var executor = new MongoMigrationExecutor(db.Database);
        var builder = new MigrationBuilder();
        builder.For<Product>(product => product.ConvertField(x => x.Quantity, MigrationFieldType.String, MigrationFieldType.Int32));
        await executor.ApplyAsync(builder.Operations);

        var stored = await raw.Find(Builders<BsonDocument>.Filter.Eq("_id", "p1")).FirstAsync();
        stored["Quantity"].BsonType.Should().Be(BsonType.Int32);
        stored["Quantity"].AsInt32.Should().Be(5);
    }

    [Fact]
    public async Task RenameField_renames_across_documents()
    {
        using var db = fixture.NewDatabase();
        var raw = db.Database.GetCollection<BsonDocument>("Product");
        await raw.InsertOneAsync(new BsonDocument { { "_id", "p1" }, { "Name", "Widget" } });

        var executor = new MongoMigrationExecutor(db.Database);
        var builder = new MigrationBuilder();
        builder.For<Product>(product => product.RenameField(x => x.Name, "DisplayName"));
        await executor.ApplyAsync(builder.Operations);

        var stored = await raw.Find(Builders<BsonDocument>.Filter.Eq("_id", "p1")).FirstAsync();
        stored.Contains("DisplayName").Should().BeTrue();
        stored.Contains("Name").Should().BeFalse();
        stored["DisplayName"].AsString.Should().Be("Widget");
    }

    [Fact]
    public async Task Update_sets_the_field_on_matching_documents()
    {
        using var db = fixture.NewDatabase();
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
        migrated.Should().Be(2);
    }
}
