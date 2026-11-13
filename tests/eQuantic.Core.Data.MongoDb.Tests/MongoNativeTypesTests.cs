using System.Text.Json;
using System.Text.Json.Nodes;
using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.Repository;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>A run's state, stored as its labels by the annotation on the enum.</summary>
[EnumType]
public enum RunState
{
    Queued,
    [StoredAs("in_progress")]
    InProgress,
    Completed,
}

/// <summary>A delivery channel, stored as its labels by the model builder's <c>Enum&lt;Channel&gt;()</c>.</summary>
public enum Channel
{
    Email,
    Sms,
}

/// <summary>A weekday, kept as a plain enum: stored as its number.</summary>
public enum Weekday
{
    Monday = 1,
    Tuesday = 2,
}

/// <summary>Enums stored as labels, JSON members, and members the store fills (by annotation).</summary>
public sealed class RunRecord : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public RunState State { get; set; }

    public RunState? Previous { get; set; }

    public Channel Channel { get; set; }

    public Weekday Day { get; set; }

    public JsonDocument? Payload { get; set; }

    public JsonNode? Steps { get; set; }

    public JsonElement? Meta { get; set; }

    [StoreGenerated]
    public long Sequence { get; set; }

    [StoreGenerated]
    public DateTime RecordedAt { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>Members the store fills, declared by the fluent model instead of annotations.</summary>
public sealed class Receipt : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Note { get; set; } = "";

    public int Number { get; set; }

    public DateTimeOffset IssuedAt { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>An enum the driver serializes before anything declares it stored as labels.</summary>
public enum LateDeclared
{
    One,
    Two,
}

/// <summary>
///     Enums stored as labels, JSON members and store-generated members against a real MongoDB: the label as a BSON
///     string (filtered through the LINQ pipeline and through the driver's filter translation of a set-based write),
///     JSON as embedded BSON, a sequence from an atomic <c>$inc</c> counter and the server's clock read once per
///     commit, and a replace that keeps what the store generated.
/// </summary>
[TestFixture]
public sealed class MongoNativeTypesTests : MongoIntegrationTest
{
    private static readonly MongoModel Model = new MongoModelBuilder()
        .Enum<Channel>()
        .Entity<RunRecord>(_ => { })
        .Entity<Receipt>(entity => entity
            .Generated(x => x.Number)
            .Generated(x => x.IssuedAt, StoreValue.Clock))
        .Build();

    /// <summary>The driver's serializers are global and fixed on first use: the model is declared before any test writes.</summary>
    [OneTimeSetUp]
    public void DeclareTheModel() => Assert.That(Model, Is.Not.Null);

    private static IAsyncRepository<RunRecord, string> Runs(MongoTestDatabase db) =>
        db.Resolve<IAsyncRepository<RunRecord, string>>();

    private static Task<BsonDocument> Raw(MongoTestDatabase db, string collection, string id) =>
        db.Database.GetCollection<BsonDocument>(collection).Find(Builders<BsonDocument>.Filter.Eq("_id", id)).FirstAsync();

    [Test]
    public async Task Enums_are_stored_as_labels()
    {
        using var db = NewDatabase();
        var repo = Runs(db);
        var running = new RunRecord { State = RunState.InProgress, Channel = Channel.Sms, Day = Weekday.Tuesday };
        var done = new RunRecord { State = RunState.Completed, Previous = RunState.InProgress, Channel = Channel.Email, Day = Weekday.Monday };
        await repo.AddAsync(running);
        await repo.AddAsync(done);
        await Uow(db).CommitAsync();

        var raw = await Raw(db, "RunRecord", running.Id);
        Assert.That(raw["State"], Is.EqualTo(new BsonString("in_progress")), "the document holds the label");
        Assert.That(raw["Channel"], Is.EqualTo(new BsonString("Sms")), "the fluent declaration stores the label too");
        Assert.That(raw["Day"], Is.EqualTo(new BsonInt32(2)), "a plain enum still holds its number");
        Assert.That(raw["Previous"], Is.EqualTo(BsonNull.Value));
        Assert.That((await Raw(db, "RunRecord", done.Id))["Previous"], Is.EqualTo(new BsonString("in_progress")));

        var loaded = (await repo.GetAsync(running.Id))!;
        Assert.That(loaded.State, Is.EqualTo(RunState.InProgress));
        Assert.That(loaded.Previous, Is.Null);
        Assert.That(loaded.Channel, Is.EqualTo(Channel.Sms));
        Assert.That(loaded.Day, Is.EqualTo(Weekday.Tuesday));

        // The LINQ path: the driver's aggregation pipeline.
        var ids = new[] { running.Id, done.Id };
        Assert.That((await repo.GetFilteredAsync(x => ids.Contains(x.Id) && x.State == RunState.InProgress)).Single().Id,
            Is.EqualTo(running.Id));
        Assert.That((await repo.GetFilteredAsync(x => ids.Contains(x.Id) && x.Previous == RunState.InProgress)).Single().Id,
            Is.EqualTo(done.Id));
        Assert.That((await repo.GetFilteredAsync(x => ids.Contains(x.Id) && x.Channel != Channel.Sms)).Single().Id,
            Is.EqualTo(done.Id));
        Assert.That((await repo.GetFilteredAsync(x => ids.Contains(x.Id) && (int)x.State == 1)).Single().Id,
            Is.EqualTo(running.Id), "the compiler's own shape of a comparison with a member compares labels");

        loaded.State = RunState.Completed;
        await repo.ModifyAsync(loaded);
        await Uow(db).CommitAsync();
        Assert.That((await repo.GetAsync(running.Id))!.State, Is.EqualTo(RunState.Completed), "an update writes the label");
        Assert.That((await Raw(db, "RunRecord", running.Id))["State"], Is.EqualTo(new BsonString("Completed")));

        // The filter translation of a set-based write: its filter and its $set both compare and write labels.
        var updated = await repo.UpdateManyAsync(x => x.Id == done.Id && x.Previous == RunState.InProgress,
            x => new RunRecord { State = RunState.Queued });
        Assert.That(updated, Is.EqualTo(1L));
        Assert.That((await Raw(db, "RunRecord", done.Id))["State"], Is.EqualTo(new BsonString("Queued")),
            "a set-based update writes the label too");

        var removed = await repo.DeleteManyAsync(x => ids.Contains(x.Id) && x.State == RunState.Queued);
        Assert.That(removed, Is.EqualTo(1L), "a set-based delete filters by the label");
        Assert.That(await repo.GetAsync(done.Id), Is.Null);
    }

    [Test]
    public async Task Json_members_round_trip_as_embedded_bson()
    {
        using var db = NewDatabase();
        var repo = Runs(db);
        using var payload = JsonDocument.Parse("""{"name":"build","matrix":{"os":["linux","macos"]},"retries":2}""");
        var record = new RunRecord
        {
            Payload = payload,
            Steps = JsonNode.Parse("""[1,"two",{"three":3},null,true,2.5]"""),
            Meta = JsonDocument.Parse("\"just a string\"").RootElement.Clone(),
        };
        var empty = new RunRecord();
        await repo.AddAsync(record);
        await repo.AddAsync(empty);
        await Uow(db).CommitAsync();

        var raw = await Raw(db, "RunRecord", record.Id);
        Assert.That(raw["Payload"].IsBsonDocument, Is.True, "an object is an embedded document");
        Assert.That(raw["Payload"]["matrix"]["os"][1], Is.EqualTo(new BsonString("macos")));
        Assert.That(raw["Payload"]["retries"], Is.EqualTo(new BsonInt32(2)));
        Assert.That(raw["Steps"].IsBsonArray, Is.True, "an array is a BSON array");
        Assert.That(raw["Steps"][2]["three"], Is.EqualTo(new BsonInt32(3)));
        Assert.That(raw["Meta"], Is.EqualTo(new BsonString("just a string")), "a scalar is the BSON scalar");

        var loaded = (await repo.GetAsync(record.Id))!;
        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(loaded.Payload!.RootElement.GetRawText()),
            JsonNode.Parse(payload.RootElement.GetRawText())), Is.True, "the object comes back equal");
        Assert.That(JsonNode.DeepEquals(loaded.Steps, record.Steps), Is.True, "the array comes back equal");
        Assert.That(loaded.Meta?.GetString(), Is.EqualTo("just a string"), "a scalar comes back equal");

        var blank = (await repo.GetAsync(empty.Id))!;
        Assert.That(blank.Payload, Is.Null);
        Assert.That(blank.Steps, Is.Null);
        Assert.That(blank.Meta, Is.Null);

        loaded.Steps = new JsonArray(JsonValue.Create("rewritten"));
        await repo.ModifyAsync(loaded);
        await Uow(db).CommitAsync();
        Assert.That((await repo.GetAsync(record.Id))!.Steps!.ToJsonString(), Is.EqualTo("""["rewritten"]"""));
    }

    [Test]
    public async Task Store_generated_members_are_the_stores()
    {
        using var db = NewDatabase();
        var repo = Runs(db);
        var before = DateTime.UtcNow.AddMinutes(-1);
        var first = new RunRecord();
        var second = new RunRecord();
        await repo.AddAsync(first);
        await repo.AddAsync(second);
        await Uow(db).CommitAsync();

        Assert.That(first.Sequence, Is.GreaterThan(0L), "the sequence was read back onto the entity");
        Assert.That(second.Sequence, Is.Not.EqualTo(first.Sequence), "each document took its own number");
        Assert.That(first.RecordedAt, Is.GreaterThan(before), "the clock was read back onto the entity");
        Assert.That(second.RecordedAt, Is.EqualTo(first.RecordedAt), "one commit, one instant of the server's clock");

        var raw = await Raw(db, "RunRecord", first.Id);
        Assert.That(raw["Sequence"].ToInt64(), Is.EqualTo(first.Sequence));
        var counter = await Raw(db, MongoSequences.CollectionName, "RunRecord.Sequence");
        Assert.That(counter["value"].ToInt64(), Is.EqualTo(Math.Max(first.Sequence, second.Sequence)),
            "the counter document holds the last number it handed out");

        var stored = (await repo.GetAsync(first.Id))!;
        Assert.That(stored.RecordedAt, Is.EqualTo(first.RecordedAt).Within(TimeSpan.FromMilliseconds(1)));

        stored.Sequence = 999_999;
        stored.RecordedAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        stored.Channel = Channel.Sms;
        await repo.ModifyAsync(stored);
        await Uow(db).CommitAsync();

        var after = (await repo.GetAsync(first.Id))!;
        Assert.That(after.Channel, Is.EqualTo(Channel.Sms), "the update ran");
        Assert.That(after.Sequence, Is.EqualTo(first.Sequence), "the update left the sequence alone");
        Assert.That(after.RecordedAt, Is.EqualTo(first.RecordedAt).Within(TimeSpan.FromMilliseconds(1)));

        var updated = await repo.UpdateManyAsync(x => x.Id == first.Id, x => new RunRecord { Channel = Channel.Email });
        Assert.That(updated, Is.EqualTo(1L));
        var afterSetBased = (await repo.GetAsync(first.Id))!;
        Assert.That(afterSetBased.Sequence, Is.EqualTo(first.Sequence), "a set-based update writes only what it names");
        Assert.That(afterSetBased.RecordedAt, Is.EqualTo(first.RecordedAt).Within(TimeSpan.FromMilliseconds(1)));

        var third = new RunRecord();
        await repo.AddAsync(third);
        await Uow(db).CommitAsync();
        Assert.That(third.Sequence, Is.GreaterThan(Math.Max(first.Sequence, second.Sequence)), "the counter keeps counting");
    }

    [Test]
    public async Task A_sequence_is_taken_inside_the_open_transaction()
    {
        using var db = NewDatabase();
        var repo = Runs(db);
        var uow = Uow(db);

        // The counter collection exists first: a first use inside the transaction would create it there.
        await repo.AddAsync(new RunRecord());
        await uow.CommitAsync();

        await uow.BeginTransactionAsync();
        var inside = new RunRecord();
        await repo.AddAsync(inside);
        await uow.CommitAsync();
        await uow.RollbackTransactionAsync();

        var counter = await Raw(db, MongoSequences.CollectionName, "RunRecord.Sequence");
        Assert.That(counter["value"].ToInt64(), Is.EqualTo(inside.Sequence - 1),
            "the counter's $inc ran in the transaction, and rolled back with it");
        Assert.That(await repo.GetAsync(inside.Id), Is.Null);
    }

    [Test]
    public async Task Fluent_declarations_generate_a_sequence_and_the_clock()
    {
        using var db = NewDatabase();
        var repo = db.Resolve<IAsyncRepository<Receipt, string>>();
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        var receipt = new Receipt { Note = "first", Number = 42, IssuedAt = DateTimeOffset.MinValue };
        await repo.AddAsync(receipt);
        await Uow(db).CommitAsync();

        Assert.That(receipt.Number, Is.EqualTo(1), "the store's number replaced the client's");
        Assert.That(receipt.IssuedAt, Is.GreaterThan(before));
        var loaded = (await repo.GetAsync(receipt.Id))!;
        Assert.That(loaded.Number, Is.EqualTo(1));
        Assert.That(loaded.IssuedAt, Is.EqualTo(receipt.IssuedAt));
    }

    [Test]
    public void Explain_names_labels_json_and_store_generated_members()
    {
        var explained = Model.Explain();

        Assert.That(explained, Does.Contain("Channel -> stored as labels, a BSON string (Email, Sms)"));
        Assert.That(explained, Does.Contain("RunState -> stored as labels, a BSON string (Queued, InProgress 'in_progress', Completed)"));
        Assert.That(explained, Does.Not.Contain("Weekday -> stored as labels"), "a plain enum keeps its number");
        Assert.That(explained, Does.Contain("member: State \"State\" (labels: Queued, in_progress, Completed)"));
        Assert.That(explained, Does.Contain("member: Previous \"Previous\" (labels: Queued, in_progress, Completed)"));
        Assert.That(explained, Does.Contain("member: Payload \"Payload\" (embedded JSON)"));
        Assert.That(explained, Does.Contain("member: Sequence \"Sequence\" store-generated (sequence: $inc on \"_sequences\" document \"RunRecord.Sequence\")"));
        Assert.That(explained, Does.Contain("member: RecordedAt \"RecordedAt\" store-generated (clock: the server's time, read once per commit)"));
        Assert.That(explained, Does.Contain("member: Number \"Number\" store-generated (sequence"));
        Assert.That(explained, Does.Contain("member: Day \"Day\"\n").Or.Contain("member: Day \"Day\"\r\n"), "a plain enum is not described as labels");
    }

    [Test]
    public void A_store_generated_id_or_wrong_type_is_refused_at_model_build()
    {
        Assert.That(() => new MongoModelBuilder().Entity<GeneratedId>(entity => entity.Generated(x => x.Id)),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("_id"));
        Assert.That(() => new MongoModelBuilder().Entity<GeneratedText>(entity => entity.Generated(x => x.Text)),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("neither"));
    }

    [Test]
    public void An_enum_already_stored_as_its_number_cannot_be_declared_labels_afterwards()
    {
        _ = BsonSerializer.LookupSerializer<LateDeclared>();

        Assert.That(() => new MongoModelBuilder().Enum<LateDeclared>(),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("already serialized as its number"));
    }

    /// <summary>An entity whose key someone tries to declare store-generated.</summary>
    public sealed class GeneratedId : IEntity<long>
    {
        public long Id { get; set; }

        public long GetKey() => Id;

        public void SetKey(long key) => Id = key;
    }

    /// <summary>An entity whose text someone tries to declare store-generated.</summary>
    public sealed class GeneratedText : IEntity<string>
    {
        public string Id { get; set; } = "";

        public string Text { get; set; } = "";

        public string GetKey() => Id;

        public void SetKey(string key) => Id = key;
    }
}
