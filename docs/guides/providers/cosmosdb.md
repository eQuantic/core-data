# Azure Cosmos DB — deep dive

Cosmos rewards partition-aware design and punishes everything else in Request Units. This provider
is built around that reality: the model declares partitioning up front, queries pin partitions
automatically when filters allow, and every mapping decision flows through **one serializer
contract** so documents and queries cannot drift apart.

## The serializer contract

The provider ships `CosmosEntitySerializer` — System.Text.Json (web defaults, camelCase) extended
with the modeling vocabulary, and registered as the SDK's `CosmosLinqSerializer` so **LINQ member
translation asks the same contract**:

- `[StoredAs("display_name")]` — the document stores `display_name`, and filters/sorts/projections
  on the member translate to `display_name`. End to end, by construction.
- `[Unmapped]` — the member never serializes, and a query touching it **throws** naming the member
  (a filter that can only match nothing is a bug, not an empty result).
- `Converts<TMember, TStored>(…)` — declared on the model builder, **type-level by design**: the
  SDK serializes filter constants by type, so only a type-level converter keeps
  `x.Status == Status.Open` comparing the stored representation.

## Partitioning

```csharp
model.Entity<Order>(entity => entity
    .Container("orders")
    .PartitionKey(x => x.Tenant)          // flat…
    .PartitionKey(x => x.Region));        // …or chained: hierarchical multi-hash (≤ 3 levels)
```

- **Partition inference**: filters are analyzed for AND-reachable equality on the partition-key
  member (captured variables included). A pinned query runs single-partition — the cheapest RU
  path — and `Explain()` reports `PartitionScoped`.
- **Hierarchical keys** (`[PartitionKey(Order = n)]` or chained fluent calls): the container is
  created multi-hash, point writes build the multi-value key transparently, inference declines
  (fans out) rather than guessing partial prefixes.
- Point writes always carry the key read from the entity — you never pass a `PartitionKey` by hand.

## Writes, ETags, patches

- Commits flush as batched point writes with **bulk execution** enabled.
- `[ConcurrencyToken]` on a `string` member (carrying `_etag`) turns `Modify`/`Merge` into
  conditional replaces (`If-Match`) — see [Concurrency](../writing/concurrency.md).
- `UpdateManyAsync` renders **patch operations** (`Set`, `Increment`, array append/remove) — the
  provider queries the affected ids and patches per item, the closest native form Cosmos offers.
- `BeginTransactionAsync` gives a `TransactionalBatch` — single partition, enforced with guidance.

## What is honestly rejected

- **Typed `GroupByAsync`**: the SDK's LINQ emits `SELECT VALUE {…}`, which Cosmos SQL cannot
  combine with `GROUP BY`. The provider validates the shape (so the contract stays uniform) and
  throws pointing to the alternatives. A hand-built SQL pushdown is on the roadmap.
- **Cross-container UNION**: no native mechanism exists; combine branches client-side, consciously.
- Per-document data migrations on hierarchical-key entities point to `Run(...)` (there is no single
  partition field to read back).

## TTL

`[TimeToLive(seconds)]` / `.TimeToLive(span)` sets the container's `DefaultTimeToLive`: documents
expire that long after their **last write** unless they override it. See
[Time to live](../writing/ttl.md) for the cross-store semantics table.

## Registration recap

```csharp
services.AddCosmosDatabase(connectionString, databaseName, model => { /* entities */ });
services.AddCosmosRepositories();
services.AddCosmosMigrations(assemblies);
// manual hosting: CosmosClientFactory.Create(connectionString, model) — same serializer contract
```
