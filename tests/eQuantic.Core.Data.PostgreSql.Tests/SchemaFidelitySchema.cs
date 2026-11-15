using System.Text.Json.Nodes;
using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.PostgreSql.Tests;

/// <summary>
///     A JSON member beside members whose types all convert implicitly to <c>JsonNode</c>: the shape that made the
///     generated accessor read every member as JSON.
/// </summary>
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

/// <summary>Required members and dates stored without their zone, the way a schema another tool created holds them.</summary>
public sealed class StampRow : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Facet(Required = true)]
    public string Name { get; set; } = "";

    public string? Note { get; set; }

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

/// <summary>The mapping of the schema-fidelity entities.</summary>
internal static class SchemaFidelitySchema
{
    public static RelationalModelBuilder Configure(RelationalModelBuilder builder) => builder
        .Entity<MixedJsonRow>(_ => { })
        .Entity<StampRow>(_ => { });
}

/// <summary>Creates the schema-fidelity tables.</summary>
[Migration("Schema fidelity", 2026, 11, 15, 0, 0, 0)]
public sealed class SchemaFidelityMigration : Data.Migration.Migration
{
    /// <inheritdoc />
    public override void Up(IMigrationBuilder migration) => migration
        .For<MixedJsonRow>(row => row.EnsureCollection())
        .For<StampRow>(row => row.EnsureCollection());
}
