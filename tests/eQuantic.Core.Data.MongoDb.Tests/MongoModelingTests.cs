using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.Repository;
using MongoDB.Bson;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>An entity modeled entirely by the store-neutral annotations — no driver attributes anywhere.</summary>
[Entity("annotated_notes")]
public sealed class AnnotatedNote : IEntity<string>
{
    public string Id { get; set; } = default!;

    [StoredAs("t")]
    public string Title { get; set; } = "";

    [Unmapped]
    public string Scratch { get; set; } = "";

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>
///     Proves the eQuantic modeling vocabulary against a real MongoDB: <c>[Entity]</c> names the collection,
///     <c>[StoredAs]</c> the BSON element, <c>[Unmapped]</c> excludes — and queries render against the stored
///     names, with no <c>[BsonElement]</c> in sight.
/// </summary>
[TestFixture]
public sealed class MongoModelingTests : MongoIntegrationTest
{
    [Test]
    public async Task Annotations_drive_the_collection_and_element_names()
    {
        using var db = NewDatabase();
        var repo = db.Resolve<IAsyncRepository<AnnotatedNote, string>>();

        var note = new AnnotatedNote { Id = "n1", Title = "hello", Scratch = "volatile" };
        await repo.AddAsync(note);
        await Uow(db).CommitAsync();

        var raw = await db.Database.GetCollection<BsonDocument>("annotated_notes")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", "n1")).FirstAsync();
        Assert.That(raw.Contains("t"), Is.True, "[StoredAs] named the element — no driver attribute involved");
        Assert.That(raw.Contains("Title"), Is.False);
        Assert.That(raw.Contains("Scratch"), Is.False, "[Unmapped] kept the member out of the document");

        var found = await repo.GetFilteredAsync(x => x.Title == "hello");
        Assert.That(found.Single().Id, Is.EqualTo("n1"), "queries render against the stored element name");
    }

    [Test]
    public async Task EntityKey_annotation_makes_the_member_the_document_id()
    {
        using var db = NewDatabase();
        var repo = db.Resolve<IAsyncRepository<KeyedNote, string>>();

        await repo.AddAsync(new KeyedNote { Code = "k1", Body = "keyed" });
        await Uow(db).CommitAsync();

        var raw = await db.Database.GetCollection<BsonDocument>("keyed_notes")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", "k1")).FirstOrDefaultAsync();
        Assert.That(raw, Is.Not.Null, "[EntityKey] stored the member as _id — no duplicate ObjectId key");
        Assert.That(raw.Contains("Code"), Is.False, "the member does not persist twice");

        var found = await repo.GetAsync("k1");
        Assert.That(found?.Body, Is.EqualTo("keyed"), "point lookups resolve through the id member");
    }
}

/// <summary>An entity whose key member is not named <c>Id</c> — <c>[EntityKey]</c> declares it.</summary>
[Entity("keyed_notes")]
public sealed class KeyedNote : IEntity<string>
{
    [EntityKey]
    public string Code { get; set; } = default!;

    public string Body { get; set; } = "";

    public string GetKey() => Code;

    public void SetKey(string key) => Code = key;
}
