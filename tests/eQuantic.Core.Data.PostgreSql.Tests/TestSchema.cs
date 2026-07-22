using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.PostgreSql.Tests;

/// <summary>An order entity exercising the type surface: strings, nullable strings, decimals, arrays, UTC timestamps and navigations.</summary>
public sealed class SaleOrder : IEntity<Guid>
{
    public Guid Id { get; set; }

    public string Customer { get; set; } = "";

    public string? Status { get; set; }

    public decimal Total { get; set; }

    public int Quantity { get; set; }

    public List<string> Tags { get; set; } = [];

    public DateTime CreatedAt { get; set; }

    public Guid BuyerId { get; set; }

    public Buyer? Buyer { get; set; }

    public List<OrderItem> Items { get; set; } = [];

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A referenced entity for the reference-include shape (<c>SaleOrder.Buyer</c> via <c>BuyerId</c>).</summary>
public sealed class Buyer : IEntity<Guid>
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>An element entity for the collection-include shape (<c>SaleOrder.Items</c> via <c>SaleOrderId</c>).</summary>
public sealed class OrderItem : IEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid SaleOrderId { get; set; }

    public string Product { get; set; } = "";

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
        .Entity<Buyer>(_ => { })
        .Entity<OrderItem>(_ => { })
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
        .For<Buyer>(buyer => buyer
            .EnsureCollection())
        .For<OrderItem>(item => item
            .EnsureCollection())
        .For<Ticket>(ticket => ticket
            .EnsureCollection());
}
