using System.Text.Json.Nodes;
using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.CosmosDb.Tests;

/// <summary>A JSON member beside members whose types all convert implicitly to <c>JsonNode</c>.</summary>
public sealed class MixedJsonRow : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Category { get; set; } = "";

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

    public string Category { get; set; } = "";

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
public sealed class CosmosTextDate : IEntity<string>
{
    public string Id { get; set; } = "";

    public string Category { get; set; } = "";

    [DateStorage(WithoutZone = true)]
    public string When { get; set; } = "";

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>A date declared at the precision of a type-level conversion that also claims it.</summary>
public sealed class CosmosConvertedDate : IEntity<string>
{
    public string Id { get; set; } = "";

    public string Category { get; set; } = "";

    [DateStorage(WithoutZone = true)]
    public DateTimeOffset At { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>
///     What mapping a schema another tool created needs, against the Cosmos DB emulator: a JSON member that leaves the
///     other members alone, required members (which a document cannot enforce, and <c>Explain()</c> says so), and
///     dates declared without their zone, rounded and read back as UTC as on every engine.
/// </summary>
[TestFixture]
public sealed class CosmosSchemaFidelityTests : CosmosIntegrationTest
{
    [Test]
    public async Task The_other_members_of_an_entity_with_a_JsonNode_member_keep_their_own_types()
    {
        var repo = Resolve<IAsyncRepository<MixedJsonRow, string>>();
        var at = new DateTime(2026, 11, 15, 10, 20, 30, DateTimeKind.Utc);
        var row = new MixedJsonRow
        {
            Category = Partition, Count = 7, Name = "seven", Active = true, At = at, Data = JsonNode.Parse("""{"a":[1,2]}"""),
        };

        await repo.AddAsync(row);
        await Uow.CommitAsync();

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
        var repo = Resolve<IAsyncRepository<StampRow, string>>();
        var row = new StampRow
        {
            Category = Partition,
            Name = "round",
            CreatedAt = new DateTime(2026, 11, 15, 10, 20, 30, 123, DateTimeKind.Utc).AddTicks(5000),
            ClosedAt = new DateTime(2026, 11, 15, 7, 20, 30, 999, DateTimeKind.Unspecified).AddTicks(5000),
            SeenAt = new DateTimeOffset(2026, 11, 15, 7, 20, 30, 250, TimeSpan.FromHours(-3)),
        };
        var empty = new StampRow { Category = Partition, Name = "empty", CreatedAt = DateTime.UtcNow };
        await repo.AddAsync(row);
        await repo.AddAsync(empty);
        await Uow.CommitAsync();

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
    }

    [Test]
    public void A_date_declaration_Cosmos_DB_cannot_apply_is_refused_when_the_model_is_built()
    {
        Assert.That(() => new CosmosModelBuilder().Entity<CosmosTextDate>(entity => entity.PartitionKey(x => x.Category)),
            Throws.InvalidOperationException.With.Message.Contain("CosmosTextDate.When").And.Message.Contain("DateTime or DateTimeOffset"));
        Assert.That(() => new CosmosModelBuilder()
                .Converts<DateTimeOffset, long>(at => at.UtcTicks, ticks => new DateTimeOffset(ticks, TimeSpan.Zero))
                .Entity<CosmosConvertedDate>(entity => entity.PartitionKey(x => x.Category))
                .Build(),
            Throws.InvalidOperationException.With.Message.Contain("CosmosConvertedDate.At").And.Message.Contain("Converts"));
    }

    [Test]
    public void Explain_says_a_required_member_is_not_enforced_and_how_a_date_is_stored()
    {
        var explained = Resolve<CosmosModel>().Explain();

        Assert.That(explained, Does.Contain("required: Name (not enforced: a Cosmos DB document has no column nullability)"));
        Assert.That(explained, Does.Contain("date: CreatedAt (without zone, UTC, 3 fractional digits, an ISO 8601 string)"));
    }
}
