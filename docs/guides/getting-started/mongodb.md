# Getting started — MongoDB

The MongoDB provider implements the eQuantic.Core.Data contracts directly on the
[official MongoDB driver](https://www.mongodb.com/docs/drivers/csharp/) — with first-class
document-store migrations and **no `[BsonElement]` (or any driver attribute) on your entities**.

## 1. Install

```bash
dotnet add package eQuantic.Core.Data.MongoDb
```

## 2. Define an entity

```csharp
using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.Repository;

[Entity("products")]                     // collection name (defaults to the type name)
public sealed class Product : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [StoredAs("n")] public string Name { get; set; } = "";     // BSON element name
    public string Category { get; set; } = "";
    public decimal Price { get; set; }

    public string GetKey() => Id;
    public void SetKey(string key) => Id = key;
}
```

The member named `Id` — or any member annotated `[EntityKey]` — becomes the document's `_id`.

## 3. Register

```csharp
services.AddMongoRepositories(connectionString, "shop");

// or with the fluent model (renames, converters, keys, TTL, ordered reads):
services.AddMongoRepositories(connectionString, "shop", model => model
    .Entity<Product>(entity => entity
        .Converts(x => x.Price, price => (double)price, stored => (decimal)stored)));

services.AddMongoMigrations(typeof(Program).Assembly);
```

## 4. Schema and usage

MongoDB is schemaless, but **collections, indexes and TTL are not** — migrations own them:

```csharp
[Migration("Products setup", 2026, 7, 22, 12, 0, 0)]
public sealed class ProductsSetup : Migration
{
    public override void Up(IMigrationBuilder migration) => migration
        .For<Product>(product => product
            .EnsureCollection()
            .Index(x => x.Category)
            .Index(x => x.Name, o => o.Text())                       // text index
            .Index(x => x.Price, o => o.Filtered(x => x.Price > 0))); // partial index
}
```

```csharp
await repository.AddAsync(new Product { Name = "Keyboard", Category = "Peripherals", Price = 49.90m });
await unitOfWork.CommitAsync();     // one ordered BulkWrite per collection

var cheap = await repository.GetFilteredAsync(p => p.Price < 30m);

// set-based updates render as native update operators ($set, $inc, $push...)
await repository.UpdateManyAsync(
    p => p.Category == "Peripherals",
    p => new Product { Price = p.Price * 0.9m });
```

Filters, sorts and projections **render against the stored element names** — `[StoredAs("n")]`
means the query says `{ "n": ... }`, never a silent full scan over a mismatched name.

## Multi-document transactions

```csharp
await unitOfWork.BeginTransactionAsync();     // requires a replica set
// ... stage writes; reads inside the transaction see its writes
await unitOfWork.CommitAsync();
await unitOfWork.CommitTransactionAsync();
```

## Where next

- [MongoDB deep dive](../providers/mongodb.md) — aggregations, `$unionWith`, includes, concurrency,
  TTL indexes.
- [Modeling](../modeling/overview.md) · [Migrations](../migrations/index.md)
