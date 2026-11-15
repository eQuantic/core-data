using System.Text.Json.Nodes;
using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.Repository;
using MongoDB.Bson;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>A JSON member beside members whose types all convert implicitly to <c>JsonNode</c>.</summary>
public sealed class MixedJsonRow : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public int Count { get; set; }

    public string Name { get; set; } = "";

    public bool Active { get; set; }

    public DateTime At { get; set; }

    public JsonNode? Data { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>Required members and dates declared without their zone.</summary>
public sealed class StampRow : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [Facet(Required = true)]
    public string Name { get; set; } = "";

    [Facet(Required = true)]
    [DateStorage(WithoutZone = true, Precision = 3)]
    public DateTime CreatedAt { get; set; }

    [DateStorage(WithoutZone = true, Precision = 3)]
    public DateTime? ClosedAt { get; set; }

    [DateStorage(WithoutZone = true)]
    public DateTimeOffset? SeenAt { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>A date declaration on a member that is not a date.</summary>
public sealed class MongoTextDate
{
    public string Id { get; set; } = "";

    [DateStorage(WithoutZone = true)]
    public string When { get; set; } = "";
}

/// <summary>A date declaration finer than MongoDB's milliseconds.</summary>
public sealed class MongoFineDate
{
    public string Id { get; set; } = "";

    [DateStorage(WithoutZone = true, Precision = 4)]
    public DateTime At { get; set; }
}

/// <summary>A member declared with a date storage and a converter of its own.</summary>
public sealed class MongoConvertedDate
{
    public string Id { get; set; } = "";

    public DateTime At { get; set; }
}

/// <summary>
///     What mapping a schema another tool created needs, against a real MongoDB: a JSON member that leaves the other
///     members alone, required members (which a document cannot enforce, and <c>Explain()</c> says so), and dates
///     declared without their zone, rounded and read back as UTC as on every engine.
/// </summary>
[TestFixture]
public sealed class MongoSchemaFidelityTests : MongoIntegrationTest
{
    private static readonly MongoModel Model = new MongoModelBuilder()
        .Entity<MixedJsonRow>(_ => { })
        .Entity<StampRow>(_ => { })
        .Build();

    /// <summary>The driver's serializers are global and fixed on first use: the model is declared before any test writes.</summary>
    [OneTimeSetUp]
    public void DeclareTheModel() => Assert.That(Model, Is.Not.Null);

    [Test]
    public async Task The_other_members_of_an_entity_with_a_JsonNode_member_keep_their_own_types()
    {
        using var db = NewDatabase();
        var repo = db.Resolve<IAsyncRepository<MixedJsonRow, string>>();
        var at = new DateTime(2026, 11, 15, 10, 20, 30, DateTimeKind.Utc);
        var row = new MixedJsonRow { Count = 7, Name = "seven", Active = true, At = at, Data = JsonNode.Parse("""{"a":[1,2]}""") };

        await repo.AddAsync(row);
        await Uow(db).CommitAsync();

        var read = (await repo.GetAsync(row.Id))!;
        Assert.That(read.Count, Is.EqualTo(7));
        Assert.That(read.Name, Is.EqualTo("seven"));
        Assert.That(read.Active, Is.True);
        Assert.That(read.At, Is.EqualTo(at));
        Assert.That(JsonNode.DeepEquals(read.Data, row.Data), Is.True);
    }

    [Test]
    public async Task A_date_without_its_zone_is_rounded_half_away_from_zero_and_read_back_as_utc()
    {
        using var db = NewDatabase();
        var repo = db.Resolve<IAsyncRepository<StampRow, string>>();
        var row = new StampRow
        {
            Name = "round",
            CreatedAt = new DateTime(2026, 11, 15, 10, 20, 30, 123, DateTimeKind.Utc).AddTicks(5000),
            ClosedAt = new DateTime(2026, 11, 15, 7, 20, 30, 999, DateTimeKind.Unspecified).AddTicks(5000),
            SeenAt = new DateTimeOffset(2026, 11, 15, 7, 20, 30, 250, TimeSpan.FromHours(-3)),
        };
        var empty = new StampRow { Name = "empty", CreatedAt = DateTime.UtcNow };
        await repo.AddAsync(row);
        await repo.AddAsync(empty);
        await Uow(db).CommitAsync();

        var raw = await db.Database.GetCollection<BsonDocument>("StampRow")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", row.Id)).FirstAsync();
        Assert.That(raw["SeenAt"].BsonType, Is.EqualTo(BsonType.DateTime), "a DateTimeOffset declared so is a BSON date");
        Assert.That(raw["CreatedAt"].ToUniversalTime(), Is.EqualTo(new DateTime(2026, 11, 15, 10, 20, 30, 124, DateTimeKind.Utc)));

        var back = (await repo.GetAsync(row.Id))!;
        Assert.That(back.CreatedAt, Is.EqualTo(new DateTime(2026, 11, 15, 10, 20, 30, 124, DateTimeKind.Utc)),
            ".1235 rounds up, half away from zero, as on every engine");
        Assert.That(back.CreatedAt.Kind, Is.EqualTo(DateTimeKind.Utc));
        Assert.That(back.ClosedAt, Is.EqualTo(new DateTime(2026, 11, 15, 7, 20, 31, 0, DateTimeKind.Utc)),
            "an Unspecified value is taken as UTC");
        Assert.That(back.SeenAt, Is.EqualTo(new DateTimeOffset(2026, 11, 15, 10, 20, 30, 250, TimeSpan.Zero)));
        Assert.That(back.SeenAt!.Value.Offset, Is.EqualTo(TimeSpan.Zero));
        var blank = (await repo.GetAsync(empty.Id))!;
        Assert.That(blank.ClosedAt, Is.Null);
        Assert.That(blank.SeenAt, Is.Null);

        var halfPast = new DateTime(2026, 11, 15, 10, 20, 30, 500, DateTimeKind.Utc);
        var found = await repo.GetFilteredAsync(x => x.Id == row.Id && x.CreatedAt < halfPast);
        Assert.That(found.Count(), Is.EqualTo(1), "a filter compares the member as UTC");
    }

    [Test]
    public void A_date_declaration_MongoDB_cannot_keep_is_refused_when_the_model_is_built()
    {
        Assert.That(() => new MongoModelBuilder().Entity<MongoTextDate>(_ => { }),
            Throws.InvalidOperationException.With.Message.Contain("MongoTextDate.When").And.Message.Contain("DateTime or DateTimeOffset"));
        Assert.That(() => new MongoModelBuilder().Entity<MongoFineDate>(_ => { }),
            Throws.InvalidOperationException.With.Message.Contain("MongoFineDate.At").And.Message.Contain("MongoDB keeps dates to 3"));
        Assert.That(() => new MongoModelBuilder().Entity<MongoConvertedDate>(entity => entity
                .Converts(x => x.At, at => at.Ticks, ticks => new DateTime(ticks, DateTimeKind.Utc))
                .DateStorage(x => x.At, withoutZone: true)),
            Throws.InvalidOperationException.With.Message.Contain("MongoConvertedDate.At").And.Message.Contain("converter"));
    }

    [Test]
    public void Explain_says_a_required_member_is_not_enforced_and_how_a_date_is_stored()
    {
        var explained = Model.Explain();

        Assert.That(explained, Does.Contain("member: Name \"Name\" required (not enforced: a MongoDB document has no column nullability)"));
        Assert.That(explained, Does.Contain("member: CreatedAt \"CreatedAt\" (date without zone, UTC, 3 fractional digits) required"));
    }
}
