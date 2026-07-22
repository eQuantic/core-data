using System.Linq.Expressions;
using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.MongoDb.Migration;
using MongoDB.Bson;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>Covers the MongoDB migration executor and runner against a real database.</summary>
[TestFixture]
public sealed class MongoMigrationTests : MongoIntegrationTest
{
    [Test]
    public async Task Runner_applies_pending_once_and_skips_on_the_second_run()
    {
        using var db = NewDatabase(typeof(ProductsSetupMigration).Assembly);
        var runner = db.Resolve<IMigrationRunner>();

        var applied = await runner.RunAsync();
        Assert.That(applied, Is.GreaterThanOrEqualTo(2));
        Assert.That(await runner.RunAsync(), Is.EqualTo(0));

        var recorded = await db.Database.GetCollection<BsonDocument>("_migrations")
            .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        Assert.That(recorded, Has.Count.EqualTo(applied));
    }

    [Test]
    public async Task Runner_records_migrations_in_timestamp_order()
    {
        using var db = NewDatabase(typeof(ProductsSetupMigration).Assembly);
        await db.Resolve<IMigrationRunner>().RunAsync();

        var ids = (await db.Database.GetCollection<BsonDocument>("_migrations")
                .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync())
            .Select(document => document["_id"].AsString).OrderBy(id => id).ToList();

        var setup = ids.FindIndex(id => id.Contains("Products setup"));
        var backfill = ids.FindIndex(id => id.Contains("Products backfill"));
        Assert.That(setup, Is.GreaterThanOrEqualTo(0));
        Assert.That(backfill, Is.GreaterThan(setup));
    }

    [Test]
    public async Task Runner_creates_the_declared_indexes()
    {
        using var db = NewDatabase(typeof(ProductsSetupMigration).Assembly);
        await db.Resolve<IMigrationRunner>().RunAsync();

        var keys = (await Indexes(db)).Select(index => index["key"].AsBsonDocument).ToList();

        Assert.That(keys.Any(key => key.Contains("Category")), Is.True);
        Assert.That(keys.Any(key => key.Contains("Price") && key.Contains("Name")), Is.True);
    }

    [Test]
    public async Task EnsureIndex_can_be_unique()
    {
        using var db = NewDatabase();
        var builder = new MigrationBuilder();
        builder.For<Product>(product => product.Index(x => x.Name, unique: true));
        await new MongoMigrationExecutor(db.Database).ApplyAsync(builder.Operations);

        var nameIndex = (await Indexes(db)).Single(index => index["key"].AsBsonDocument.Contains("Name"));
        Assert.That(nameIndex.GetValue("unique", false).ToBoolean(), Is.True);
    }

    [Test]
    public async Task EnsureIndex_supports_ttl_and_an_explicit_name()
    {
        using var db = NewDatabase();
        var operation = new EnsureIndexOperation(typeof(Product),
            [new IndexKey((Expression<Func<Product, object>>)(p => p.Rating), false)])
        {
            ExpireAfter = TimeSpan.FromMinutes(30),
            Name = "rating_ttl",
        };
        await new MongoMigrationExecutor(db.Database).ApplyAsync([operation]);

        var ttl = (await Indexes(db)).Single(index => index.GetValue("name", "").AsString == "rating_ttl");
        Assert.That(ttl["expireAfterSeconds"].ToDouble(), Is.EqualTo(1800));
    }

    [Test]
    public async Task Index_options_build_text_and_partial_indexes()
    {
        using var db = NewDatabase();
        var builder = new MigrationBuilder();
        builder.For<Product>(product => product
            .Index(x => x.Name, options => options.Text().Named("name_text"))
            .Index(x => x.Price, options => options.Filtered(x => x.Quantity > 0).Named("price_in_stock")));
        await new MongoMigrationExecutor(db.Database).ApplyAsync(builder.Operations);

        var indexes = await Indexes(db);
        var text = indexes.Single(index => index.GetValue("name", "").AsString == "name_text");
        Assert.That(text["key"].AsBsonDocument.Contains("_fts"), Is.True, "the text index built");

        var partial = indexes.Single(index => index.GetValue("name", "").AsString == "price_in_stock");
        Assert.That(partial["partialFilterExpression"].AsBsonDocument.Contains("Quantity"), Is.True,
            "the typed predicate rendered into the partial filter");
    }

    [Test]
    public async Task Drop_field_unsets_across_documents_and_add_field_is_a_noop()
    {
        using var db = NewDatabase();
        var raw = db.Database.GetCollection<BsonDocument>("Product");
        await raw.InsertManyAsync(
        [
            new BsonDocument { { "_id", "p1" }, { "Legacy", "x" }, { "Name", "a" } },
            new BsonDocument { { "_id", "p2" }, { "Legacy", "y" }, { "Name", "b" } },
        ]);

        var builder = new MigrationBuilder();
        builder.For<Product>(product => product
            .AddField(x => x.Rating)
            .DropField("Legacy"));
        await new MongoMigrationExecutor(db.Database).ApplyAsync(builder.Operations);

        var remaining = await raw.CountDocumentsAsync(Builders<BsonDocument>.Filter.Exists("Legacy"));
        Assert.That(remaining, Is.Zero, "DropField unset the stored field across documents");
    }

    [Test]
    public async Task ConvertField_changes_the_stored_type()
    {
        using var db = NewDatabase();
        var raw = db.Database.GetCollection<BsonDocument>("Product");
        await raw.InsertOneAsync(new BsonDocument { { "_id", "p1" }, { "Quantity", "5" } });

        var builder = new MigrationBuilder();
        builder.For<Product>(product => product.ConvertField(x => x.Quantity, MigrationFieldType.String, MigrationFieldType.Int32));
        await new MongoMigrationExecutor(db.Database).ApplyAsync(builder.Operations);

        var stored = await raw.Find(Builders<BsonDocument>.Filter.Eq("_id", "p1")).FirstAsync();
        Assert.That(stored["Quantity"].BsonType, Is.EqualTo(BsonType.Int32));
        Assert.That(stored["Quantity"].AsInt32, Is.EqualTo(5));
    }

    [Test]
    public async Task RenameField_renames_across_documents()
    {
        using var db = NewDatabase();
        var raw = db.Database.GetCollection<BsonDocument>("Product");
        await raw.InsertOneAsync(new BsonDocument { { "_id", "p1" }, { "Name", "Widget" } });

        var builder = new MigrationBuilder();
        builder.For<Product>(product => product.RenameField(x => x.Name, "DisplayName"));
        await new MongoMigrationExecutor(db.Database).ApplyAsync(builder.Operations);

        var stored = await raw.Find(Builders<BsonDocument>.Filter.Eq("_id", "p1")).FirstAsync();
        Assert.That(stored.Contains("DisplayName"), Is.True);
        Assert.That(stored.Contains("Name"), Is.False);
        Assert.That(stored["DisplayName"].AsString, Is.EqualTo("Widget"));
    }

    [Test]
    public async Task Update_sets_the_field_on_matching_documents()
    {
        using var db = NewDatabase();
        var raw = db.Database.GetCollection<BsonDocument>("Product");
        await raw.InsertManyAsync(
        [
            new BsonDocument { { "_id", "p1" }, { "Category", "old" } },
            new BsonDocument { { "_id", "p2" }, { "Category", "old" } },
            new BsonDocument { { "_id", "p3" }, { "Category", "keep" } },
        ]);

        var builder = new MigrationBuilder();
        builder.For<Product>(product => product.Update(x => x.Category == "old", update => update.Set(x => x.Category, "new")));
        await new MongoMigrationExecutor(db.Database).ApplyAsync(builder.Operations);

        var migrated = await raw.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("Category", "new"));
        Assert.That(migrated, Is.EqualTo(2));
    }

    [Test]
    public async Task Run_executes_the_escape_hatch_with_the_database()
    {
        using var db = NewDatabase();
        var builder = new MigrationBuilder();
        builder.Run((context, cancellationToken) =>
            context.AsMongo().Database.GetCollection<BsonDocument>("Marker")
                .InsertOneAsync(new BsonDocument { { "_id", "ran" } }, cancellationToken: cancellationToken));
        await new MongoMigrationExecutor(db.Database).ApplyAsync(builder.Operations);

        var count = await db.Database.GetCollection<BsonDocument>("Marker")
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
        Assert.That(count, Is.EqualTo(1));
    }

    private static async Task<List<BsonDocument>> Indexes(MongoTestDatabase db) =>
        await (await db.Database.GetCollection<Product>("Product").Indexes.ListAsync()).ToListAsync();
}
