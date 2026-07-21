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

    public long Weight { get; set; }

    public double Rating { get; set; }

    public float Discount { get; set; }

    public decimal Price { get; set; }

    public List<string> Tags { get; set; } = [];

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;

    public static Product New(string name, string category, int quantity, decimal price,
        long weight = 0, double rating = 0, float discount = 0) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        Category = category,
        Quantity = quantity,
        Price = price,
        Weight = weight,
        Rating = rating,
        Discount = discount,
    };
}

/// <summary>The first sample migration exercised by the runner tests: ensures the collection and its indexes.</summary>
[Migration("Products setup", 2026, 7, 20, 10, 0, 0)]
public sealed class ProductsSetupMigration : eQuantic.Core.Data.Migration.Migration
{
    public override void Up(IMigrationBuilder migration) =>
        migration.For<Product>(product => product
            .EnsureCollection()
            .Index(x => x.Category)
            .CompositeIndex(keys => keys.Descending(x => x.Price).Ascending(x => x.Name)));
}

/// <summary>A later sample migration, used to prove the runner applies migrations in timestamp order.</summary>
[Migration("Products backfill", 2026, 7, 20, 11, 0, 0)]
public sealed class ProductsBackfillMigration : eQuantic.Core.Data.Migration.Migration
{
    public override void Up(IMigrationBuilder migration) =>
        migration.For<Product>(product => product.Index(x => x.Rating));
}
