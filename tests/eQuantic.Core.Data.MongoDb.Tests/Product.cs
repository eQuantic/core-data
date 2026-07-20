using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>A simple document entity used across the tests (collection name defaults to <c>Product</c>).</summary>
public sealed class Product : IEntity<string>
{
    public string Id { get; set; } = default!;

    public string Name { get; set; } = default!;

    public string Category { get; set; } = default!;

    public int Quantity { get; set; }

    public decimal Price { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;

    public static Product New(string name, string category, int quantity, decimal price) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        Category = category,
        Quantity = quantity,
        Price = price,
    };
}

/// <summary>A sample migration exercised by the runner tests: ensures the collection and its indexes.</summary>
[Migration("Products setup", 2026, 7, 20, 10, 0, 0)]
public sealed class ProductsSetupMigration : eQuantic.Core.Data.Migration.Migration
{
    public override void Up(IMigrationBuilder migration) =>
        migration.For<Product>(product => product
            .EnsureCollection()
            .Index(x => x.Category)
            .CompositeIndex(keys => keys.Descending(x => x.Price).Ascending(x => x.Name)));
}
