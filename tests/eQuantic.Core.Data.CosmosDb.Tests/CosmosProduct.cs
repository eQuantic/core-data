using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.CosmosDb.Tests;

/// <summary>A simple document entity used across the tests. <see cref="Category" /> is the partition key.</summary>
public sealed class CosmosProduct : IEntity<string>
{
    public string Id { get; set; } = default!;

    public string Name { get; set; } = default!;

    public string Category { get; set; } = default!;

    public int Quantity { get; set; }

    public decimal Price { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("_etag")]
    public string? ETag { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;

    public static CosmosProduct New(string name, string category, int quantity, decimal price) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        Category = category,
        Quantity = quantity,
        Price = price,
    };
}

/// <summary>A sample migration exercised by the runner tests: ensures the container and a composite index.</summary>
[Migration("Cosmos products setup", 2026, 7, 20, 12, 0, 0)]
public sealed class CosmosProductsSetupMigration : eQuantic.Core.Data.Migration.Migration
{
    public override void Up(IMigrationBuilder migration) =>
        migration.For<CosmosProduct>(product => product
            .EnsureCollection()
            .CompositeIndex(keys => keys.Descending(x => x.Price).Ascending(x => x.Name)));
}
