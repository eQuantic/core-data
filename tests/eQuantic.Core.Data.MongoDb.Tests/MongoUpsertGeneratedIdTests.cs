using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Write;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>A device keyed by an <see cref="ObjectId" /> the driver generates, unique by its serial.</summary>
public sealed class ObjectIdDevice : IEntity<ObjectId>
{
    public ObjectId Id { get; set; }

    public string Serial { get; set; } = "";

    public string Name { get; set; } = "";

    public ObjectId GetKey() => Id;

    public void SetKey(ObjectId key) => Id = key;
}

/// <summary>A device keyed by a string stored as an ObjectId, which the driver generates when it is unset.</summary>
public sealed class TextObjectIdDevice : IEntity<string>
{
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    public string Serial { get; set; } = "";

    public string Name { get; set; } = "";

    public string GetKey() => Id!;

    public void SetKey(string key) => Id = key;
}

/// <summary>
///     An upsert of an entity whose id is unset, against a real MongoDB: it is a new document, which takes the id the
///     driver generates on insert — never an empty <see cref="ObjectId" /> nor a null <c>_id</c>, which the next new
///     entity would then be merged into, or collide with.
/// </summary>
[TestFixture]
public sealed class MongoUpsertGeneratedIdTests : MongoIntegrationTest
{
    private MongoTestDatabase _db = null!;

    [SetUp]
    public async Task CreateDatabase()
    {
        _db = NewDatabase();
        foreach (var collection in new[] { nameof(ObjectIdDevice), nameof(TextObjectIdDevice) })
        {
            await _db.Database.GetCollection<BsonDocument>(collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("Serial"), new CreateIndexOptions { Unique = true, Name = "ux_serial" }));
        }
    }

    [TearDown]
    public void DropDatabase() => _db?.Dispose();

    private IUpsertRepository<TEntity, TKey> Upserts<TEntity, TKey>(IServiceScope scope) where TEntity : class, IEntity<TKey> =>
        scope.ServiceProvider.GetRequiredService<IUpsertRepository<TEntity, TKey>>();

    private Task<List<BsonDocument>> Stored(string collection) =>
        _db.Database.GetCollection<BsonDocument>(collection).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();

    [Test]
    public async Task An_unset_ObjectId_is_generated_when_upserting_on_the_key()
    {
        using var scope = _db.NewScope();
        var upserts = Upserts<ObjectIdDevice, ObjectId>(scope);

        var first = await upserts.UpsertAsync(new ObjectIdDevice { Serial = "a", Name = "first" });
        var second = await upserts.UpsertAsync(new ObjectIdDevice { Serial = "b", Name = "second" });

        Assert.That(first.Id, Is.Not.EqualTo(ObjectId.Empty), "the inserted document took a generated id");
        Assert.That(second.Id, Is.Not.EqualTo(ObjectId.Empty));
        Assert.That(second.Id, Is.Not.EqualTo(first.Id), "the second new entity is a document of its own");

        var stored = await Stored(nameof(ObjectIdDevice));
        Assert.That(stored.Select(document => document["Name"].AsString).Order(), Is.EqualTo(new[] { "first", "second" }),
            "neither entity was merged into the other");
        Assert.That(stored.Select(document => document["_id"]), Has.None.EqualTo(new BsonObjectId(ObjectId.Empty)));
    }

    [Test]
    public async Task An_unset_text_ObjectId_is_generated_when_upserting_on_the_key()
    {
        using var scope = _db.NewScope();
        var upserts = Upserts<TextObjectIdDevice, string>(scope);

        var first = await upserts.UpsertAsync(new TextObjectIdDevice { Serial = "a", Name = "first" });
        var second = await upserts.UpsertAsync(new TextObjectIdDevice { Serial = "b", Name = "second" });

        Assert.That(first.Id, Is.Not.Null.And.Not.Empty, "the inserted document took a generated id");
        Assert.That(second.Id, Is.Not.Null.And.Not.EqualTo(first.Id));

        var stored = await Stored(nameof(TextObjectIdDevice));
        Assert.That(stored.Select(document => document["Name"].AsString).Order(), Is.EqualTo(new[] { "first", "second" }),
            "neither entity was merged into the other");
        Assert.That(stored.Select(document => document["_id"].BsonType), Is.All.EqualTo(BsonType.ObjectId),
            "the _id is stored as an ObjectId, never null");
    }

    [Test]
    public async Task An_unset_ObjectId_is_generated_when_upserting_on_another_unique_key()
    {
        using var scope = _db.NewScope();
        var upserts = Upserts<ObjectIdDevice, ObjectId>(scope);

        var first = await upserts.UpsertAsync(new ObjectIdDevice { Serial = "a", Name = "first" }, x => x.Serial);
        var second = await upserts.UpsertAsync(new ObjectIdDevice { Serial = "b", Name = "second" }, x => x.Serial);
        var again = await upserts.UpsertAsync(new ObjectIdDevice { Serial = "a", Name = "renamed" }, x => x.Serial);

        Assert.That(first.Id, Is.Not.EqualTo(ObjectId.Empty));
        Assert.That(second.Id, Is.Not.EqualTo(ObjectId.Empty).And.Not.EqualTo(first.Id),
            "the second insert did not collide with the first on an empty _id");
        Assert.That(again.Id, Is.EqualTo(first.Id), "the serial found the first document, which kept its id");
        Assert.That(again.Name, Is.EqualTo("renamed"));

        var stored = await Stored(nameof(ObjectIdDevice));
        Assert.That(stored, Has.Count.EqualTo(2));
        Assert.That(stored.Select(document => document["_id"]), Has.None.EqualTo(new BsonObjectId(ObjectId.Empty)));
    }

    [Test]
    public async Task An_unset_text_ObjectId_is_generated_when_upserting_on_another_unique_key()
    {
        using var scope = _db.NewScope();
        var upserts = Upserts<TextObjectIdDevice, string>(scope);

        var first = await upserts.UpsertAsync(new TextObjectIdDevice { Serial = "a", Name = "first" }, x => x.Serial);
        var second = await upserts.UpsertAsync(new TextObjectIdDevice { Serial = "b", Name = "second" }, x => x.Serial);
        var again = await upserts.UpsertAsync(new TextObjectIdDevice { Serial = "a", Name = "renamed" }, x => x.Serial);

        Assert.That(first.Id, Is.Not.Null.And.Not.Empty);
        Assert.That(second.Id, Is.Not.Null.And.Not.EqualTo(first.Id), "the second insert did not collide with the first on a null _id");
        Assert.That(again.Id, Is.EqualTo(first.Id), "the serial found the first document, which kept its id");
        Assert.That(again.Name, Is.EqualTo("renamed"));

        var stored = await Stored(nameof(TextObjectIdDevice));
        Assert.That(stored, Has.Count.EqualTo(2));
        Assert.That(stored.Select(document => document["_id"].BsonType), Is.All.EqualTo(BsonType.ObjectId));
    }
}
