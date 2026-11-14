using System.Text.Json.Nodes;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Write;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>A JSON Schema kept as a JSON member: its keywords are names starting with <c>$</c>.</summary>
public sealed class SchemaRecord : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public JsonNode? Schema { get; set; }

    public int Revision { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>
///     A JSON member whose objects hold names starting with <c>$</c> (<c>$schema</c>, <c>$id</c>, <c>$type</c>), against a
///     real MongoDB, through every write that names the member's value in an update: a modify of the members named, an
///     upsert that inserts, an upsert that updates and a set-based update. MongoDB refuses such names as plain values of
///     <c>$set</c> and <c>$setOnInsert</c>; the JSON is written as itself all the same, and comes back equal.
/// </summary>
[TestFixture]
public sealed class MongoJsonDollarKeysTests : MongoIntegrationTest
{
    private const string SchemaText = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "https://example.test/item.json",
          "type": "object",
          "properties": { "kind": { "$ref": "#/$defs/kind" }, "tags": [ { "$type": "tag" } ] },
          "$defs": { "kind": { "enum": ["a", "b"] } }
        }
        """;

    private MongoTestDatabase _db = null!;

    [SetUp]
    public async Task CreateDatabase()
    {
        _db = NewDatabase();
        await _db.Database.GetCollection<BsonDocument>(nameof(SchemaRecord)).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("Name"), new CreateIndexOptions { Unique = true }));
    }

    [TearDown]
    public void DropDatabase() => _db?.Dispose();

    private static JsonNode Schema(string title)
    {
        var schema = JsonNode.Parse(SchemaText)!;
        schema["title"] = title;
        return schema;
    }

    private async Task<SchemaRecord> Read(string id)
    {
        using var scope = _db.NewScope();
        return (await scope.ServiceProvider.GetRequiredService<IAsyncRepository<SchemaRecord, string>>().GetAsync(id))!;
    }

    private static void AssertSchema(SchemaRecord record, string title)
    {
        Assert.That(JsonNode.DeepEquals(record.Schema, Schema(title)), Is.True,
            $"the schema comes back equal, its $-names included: {record.Schema?.ToJsonString()}");
    }

    [Test]
    public async Task A_modify_of_the_members_named_writes_the_json_as_itself()
    {
        var record = new SchemaRecord { Name = "item", Schema = Schema("first") };
        using (var seed = _db.NewScope())
        {
            await seed.ServiceProvider.GetRequiredService<IAsyncRepository<SchemaRecord, string>>().AddAsync(record);
            await seed.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync();
        }

        using (var scope = _db.NewScope())
        {
            record.Schema = Schema("modified");
            record.Revision = 2;
            await scope.ServiceProvider.GetRequiredService<IPartialModifyRepository<SchemaRecord, string>>()
                .ModifyAsync(record, x => new { x.Schema, x.Revision });
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync();
        }

        var stored = await Read(record.Id);
        AssertSchema(stored, "modified");
        Assert.That(stored.Revision, Is.EqualTo(2));
        var raw = await _db.Database.GetCollection<BsonDocument>(nameof(SchemaRecord))
            .Find(new BsonDocument("_id", record.Id)).SingleAsync();
        Assert.That(raw["Schema"]["$schema"], Is.EqualTo(new BsonString("https://json-schema.org/draft/2020-12/schema")),
            "the document holds the name as it is");
    }

    [Test]
    public async Task An_upsert_inserts_and_updates_the_json_as_itself()
    {
        using var scope = _db.NewScope();
        var upserts = scope.ServiceProvider.GetRequiredService<IUpsertRepository<SchemaRecord, string>>();

        var inserted = await upserts.UpsertAsync(new SchemaRecord { Name = "item", Schema = Schema("inserted") });
        AssertSchema(await Read(inserted.Id), "inserted");

        var updated = await upserts.UpsertAsync(new SchemaRecord { Id = inserted.Id, Name = "item", Schema = Schema("updated") });
        Assert.That(updated.Id, Is.EqualTo(inserted.Id));
        AssertSchema(await Read(inserted.Id), "updated");

        var byName = await upserts.UpsertAsync(new SchemaRecord { Name = "other", Schema = Schema("by name") }, x => x.Name);
        AssertSchema(await Read(byName.Id), "by name");
        await upserts.UpsertAsync(new SchemaRecord { Name = "other", Schema = Schema("by name again") }, x => x.Name);
        AssertSchema(await Read(byName.Id), "by name again");

        var schema = Schema("by expression");
        await upserts.UpsertAsync(new SchemaRecord { Name = "other" }, x => x.Name, x => new SchemaRecord { Schema = schema });
        AssertSchema(await Read(byName.Id), "by expression");
    }

    [Test]
    public async Task A_set_based_update_writes_the_json_as_itself()
    {
        var record = new SchemaRecord { Name = "item", Schema = Schema("first"), Revision = 1 };
        using var scope = _db.NewScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAsyncRepository<SchemaRecord, string>>();
        await repo.AddAsync(record);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync();

        var schema = Schema("set-based");
        var matched = await repo.UpdateManyAsync(x => x.Id == record.Id, x => new SchemaRecord { Schema = schema, Revision = x.Revision + 1 });

        Assert.That(matched, Is.EqualTo(1L));
        var stored = await Read(record.Id);
        AssertSchema(stored, "set-based");
        Assert.That(stored.Revision, Is.EqualTo(2), "the increment beside the JSON still applies");
    }
}
