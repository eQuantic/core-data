# Getting started — Azure Cosmos DB

The Cosmos DB provider implements the eQuantic.Core.Data contracts directly on the
[official SDK](https://learn.microsoft.com/azure/cosmos-db/nosql/sdk-dotnet-v3) — with a
serializer that keeps documents **and LINQ queries** aligned with your model, and partition-key
inference that turns filters into single-partition reads (the single biggest RU saving there is).

## 1. Install

```bash
dotnet add package eQuantic.Core.Data.CosmosDb
```

## 2. Define an entity

Cosmos needs the partition key on every point operation, so the model declares it up front:

```csharp
using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.Repository;

public sealed class Order : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [PartitionKey] public string Region { get; set; } = "";
    public string Customer { get; set; } = "";
    public decimal Total { get; set; }

    public string GetKey() => Id;
    public void SetKey(string key) => Id = key;
}
```

Documents serialize with System.Text.Json web defaults (camelCase) — `Region` stores as `region`,
the partition key path is `/region`. `[StoredAs]` renames, `[Unmapped]` excludes, and — because the
provider's serializer extends the SDK's `CosmosLinqSerializer` — **queries use the same names as
documents, always**.

## 3. Register

```csharp
services.AddCosmosDatabase(connectionString, "shop", model => model
    .Entity<Order>(entity => entity
        .Container("orders")
        .PartitionKey(x => x.Region)));
services.AddCosmosRepositories();
services.AddCosmosMigrations(typeof(Program).Assembly);
```

## 4. Containers and usage

```csharp
[Migration("Orders setup", 2026, 7, 22, 12, 0, 0)]
public sealed class OrdersSetup : Migration
{
    public override void Up(IMigrationBuilder migration) => migration
        .For<Order>(order => order.EnsureCollection());   // container + partition key + TTL from the model
}
```

```csharp
await repository.AddAsync(new Order { Region = "br", Customer = "ana", Total = 100m });
await unitOfWork.CommitAsync();      // bulk-enabled batched point writes

// this filter PINS the partition — the query runs single-partition (cheapest RU path):
var brazilian = await repository.GetFilteredAsync(o => o.Region == "br" && o.Total > 50m);
```

The provider analyzes filters for partition-key equality (`o.Region == "br"`, captured variables
included) and scopes the query to that partition automatically. No filter, no pin — the query fans
out, and that is visible in `Explain()`.

## Point writes, ETags, TTL

- `Modify`/`Merge` on an entity with a `[ConcurrencyToken]` string member becomes a **conditional
  replace** (`If-Match` on the `_etag`) — see [Optimistic concurrency](../writing/concurrency.md).
- `[TimeToLive(seconds)]` on the class sets the container's default TTL.
- Hierarchical partition keys (up to three levels) — see the
  [Cosmos deep dive](../providers/cosmosdb.md).

## Where next

- [Cosmos DB deep dive](../providers/cosmosdb.md) — the serializer contract, hierarchical keys,
  patch operations, RU thinking.
- [Modeling](../modeling/overview.md) · [Querying](../querying/pushdown.md)
