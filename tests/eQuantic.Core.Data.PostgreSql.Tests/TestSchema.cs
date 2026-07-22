using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.PostgreSql.Tests;

/// <summary>An order entity exercising the type surface: strings, nullable strings, decimals, arrays and UTC timestamps.</summary>
public sealed class SaleOrder : IEntity<Guid>
{
    public Guid Id { get; set; }

    public string Customer { get; set; } = "";

    public string? Status { get; set; }

    public decimal Total { get; set; }

    public int Quantity { get; set; }

    public List<string> Tags { get; set; } = [];

    public DateTime CreatedAt { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>An identity-keyed entity: the database generates the key and the commit reads it back.</summary>
public sealed class Ticket : IEntity<long>
{
    public long Id { get; set; }

    public string Label { get; set; } = "";

    public long GetKey() => Id;

    public void SetKey(long key) => Id = key;
}

/// <summary>The relational mapping shared by the integration tests.</summary>
internal static class TestSchema
{
    public static void Configure(RelationalModelBuilder builder) => builder
        .Entity<SaleOrder>(entity => entity.Table("sale_orders"))
        .Entity<Ticket>(entity => entity.Key(x => x.Id, generated: true));
}

/// <summary>The schema migration the runner discovers: both tables plus a customer index.</summary>
[Migration("PostgreSQL schema setup", 2026, 1, 1, 0, 0, 0)]
public sealed class SchemaSetupMigration : Data.Migration.Migration
{
    /// <inheritdoc />
    public override void Up(IMigrationBuilder migration) => migration
        .For<SaleOrder>(order => order
            .EnsureCollection()
            .Index(x => x.Customer))
        .For<Ticket>(ticket => ticket
            .EnsureCollection());
}
