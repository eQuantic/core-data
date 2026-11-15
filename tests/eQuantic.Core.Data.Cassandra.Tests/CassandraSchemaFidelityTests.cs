using System.Text.Json.Nodes;
using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.Repository;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>A JSON member beside members whose types all convert implicitly to <c>JsonNode</c>.</summary>
public sealed class MixedJsonRow : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public int Count { get; set; }

    public string Name { get; set; } = "";

    public bool Active { get; set; }

    public DateTime At { get; set; }

    public JsonNode? Data { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>Required members and dates declared without their zone.</summary>
public sealed class StampRow : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Facet(Required = true)]
    public string Name { get; set; } = "";

    [Facet(Required = true)]
    [DateStorage(WithoutZone = true, Precision = 3)]
    public DateTime CreatedAt { get; set; }

    [DateStorage(WithoutZone = true, Precision = 3)]
    public DateTime? ClosedAt { get; set; }

    [DateStorage(WithoutZone = true)]
    public DateTimeOffset? SeenAt { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A date declaration finer than Cassandra's milliseconds.</summary>
public sealed class CassandraFineDate : IEntity<Guid>
{
    public Guid Id { get; set; }

    [DateStorage(WithoutZone = true, Precision = 6)]
    public DateTime At { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>
///     What mapping a schema another tool created needs, against a real Cassandra: a JSON member that leaves the other
///     members alone, required members (which Cassandra cannot enforce outside the key, and <c>Explain()</c> says so),
///     and dates declared without their zone, rounded and read back as UTC as on every engine.
/// </summary>
[TestFixture]
public sealed class CassandraSchemaFidelityTests : CassandraIntegrationTest
{
    private static void FidelitySchema(CassandraModelBuilder builder) => builder
        .Entity<MixedJsonRow>(entity => entity.Table("mixed_json_rows").PartitionKey(x => x.Id))
        .Entity<StampRow>(entity => entity.Table("stamp_rows").PartitionKey(x => x.Id));

    private static async Task<CassandraTestDatabase> FidelityDatabaseAsync()
    {
        var db = new CassandraTestDatabase(CassandraTestServer.Host, CassandraTestServer.Port, "ks_" + Guid.NewGuid().ToString("N"),
            FidelitySchema, [typeof(SchemaSetupMigration).Assembly]);
        await db.Resolve<IMigrationExecutor>().ApplyAsync(new MigrationBuilder()
            .For<MixedJsonRow>(row => row.EnsureCollection())
            .For<StampRow>(row => row.EnsureCollection())
            .Operations);
        return db;
    }

    [Test]
    public async Task The_other_members_of_an_entity_with_a_JsonNode_member_keep_their_own_types()
    {
        using var db = await FidelityDatabaseAsync();
        var repo = db.Resolve<IAsyncRepository<MixedJsonRow, Guid>>();
        var at = new DateTime(2026, 11, 15, 10, 20, 30, DateTimeKind.Utc);
        var row = new MixedJsonRow { Count = 7, Name = "seven", Active = true, At = at, Data = JsonNode.Parse("""{"a":[1,2]}""") };

        await repo.AddAsync(row);
        await Uow(db).CommitAsync();

        var read = (await repo.GetAsync(row.Id))!;
        Assert.That(read.Count, Is.EqualTo(7));
        Assert.That(read.Name, Is.EqualTo("seven"));
        Assert.That(read.Active, Is.True);
        Assert.That(read.At.ToUniversalTime(), Is.EqualTo(at));
        Assert.That(JsonNode.DeepEquals(read.Data, row.Data), Is.True);
    }

    [Test]
    public async Task A_date_without_its_zone_is_rounded_half_away_from_zero_and_read_back_as_utc()
    {
        using var db = await FidelityDatabaseAsync();
        var repo = db.Resolve<IAsyncRepository<StampRow, Guid>>();
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
    public void A_date_declaration_Cassandra_cannot_keep_is_refused_when_the_model_is_built()
    {
        var builder = new CassandraModelBuilder();

        Assert.That(() => builder.Entity<CassandraFineDate>(entity => entity.PartitionKey(x => x.Id)),
            Throws.InvalidOperationException.With.Message.Contain("CassandraFineDate.At").And.Message.Contain("Cassandra keeps dates to 3"));
    }

    [Test]
    public async Task Explain_says_a_required_member_is_not_enforced_and_how_a_date_is_stored()
    {
        using var db = await FidelityDatabaseAsync();

        var explained = db.Resolve<CassandraModel>().Explain();

        Assert.That(explained, Does.Contain("column: Name \"Name\" (text, required (not enforced: Cassandra has no NOT NULL outside the primary key))"));
        Assert.That(explained, Does.Contain(
            "column: CreatedAt \"CreatedAt\" (timestamp without zone, UTC, 3 fractional digits, required (not enforced: Cassandra has no NOT NULL outside the primary key))"));
    }
}
