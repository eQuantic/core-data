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

## Enums as labels, JSON members, store-generated members

- **Enums as labels** — `[EnumType]` on the enum, or `model.Enum<TEnum>()`, stores each member as
  its `[StoredAs]` label (or its name) as a JSON string. The labels are a type-level converter of
  the provider's serializer, which the SDK's LINQ translation asks for every constant, so filters
  compare labels — `(int)x.State == 1`, the compiler's own shape, included — and so do patches.
  An enum declared neither way keeps its number.
- **JSON members** — `JsonDocument`, `JsonElement` and `JsonNode` are embedded in the document as
  the JSON they hold (object, array or scalar), come back equal, and `null` stays `null`.
- **Sequence** — an atomic server-side counter: one item per `<container>.<property>` in the
  `_sequences` container (partitioned by `/id`, created on first use), advanced by a `Patch`
  `Increment`, which answers the new value. One increment per counter covers a whole commit. It
  runs outside a `TransactionalBatch` (bound to one partition): a failed batch leaves a gap, never
  a repeat. On an account with provisioned throughput, the container takes its own throughput
  unless the database shares it.
- **Clock** — the server's clock, `SELECT VALUE GetCurrentDateTime()` scoped to one partition,
  read once per commit and given to every insert of it.
- **Updates keep them** — a replace writes the whole document, so `Modify` first reads the stored
  document, copies its store-generated values onto the entity and replaces it with `If-Match` on
  the `_etag` it read (reading again if another writer got in between; an entity carrying its own
  token keeps its own conflict). A `Modify` that finds no document inserts, and the store assigns
  the values. A set-based update patches only what it names.

```csharp
model.Enum<Channel>()
     .Entity<Receipt>(entity => entity
         .PartitionKey(x => x.Category)
         .Generated(x => x.Number)                          // Sequence
         .Generated(x => x.IssuedAt, StoreValue.Clock));    // Clock
```

The id and the partition key cannot be store-generated: a point write needs both before the store
answers. `Explain()` names the labels and the store-generated members.

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
  provider queries the affected ids and patches per item **under the filter**
  (`PatchItemRequestOptions.FilterPredicate`), so an item that stopped matching is left alone and the
  count is the patches applied: a compare-and-set. `DeleteManyAsync` deletes each item under the
  `_etag` of a read that still finds it matching. (Before, the patch and the delete applied
  unconditionally to whatever the query had found.)
- `UpsertAsync` reads the target and replaces (or patches) it under the `_etag` it read, or creates
  it; the id and a container's **unique key** hold a target unique within a partition, and only then
  is a concurrent upsert race-free. A 409 on a create is `DuplicateKeyException` (Cosmos DB names no
  key). See [Upserts and write outcomes](../writing/upserts.md).
- `BeginTransactionAsync` gives a `TransactionalBatch` — single partition, enforced with guidance.

## Reading shapes across documents

Cosmos DB has no join across containers, and the read shapes say what that costs instead of hiding
it ([Querying](../querying/includes.md) has the full tables):

- **Includes** load from the related container with **one second query** per path segment — the
  parents' keys read with `IN`, a shape's filter in its `WHERE`. Its order, skip and limit apply per
  parent as the rows are stitched, so every row the filter keeps travels. The related documents
  hold the foreign key (`{Parent}Id`, or `[ForeignKey("Parent")]`), and both entities are registered
  in the model.
- **Relation counts** in a projection run as one grouped query per count
  (`SELECT fk, COUNT(1) … GROUP BY fk`); the parents are read whole and the map runs in memory.
- **Nulls** (and undefined) sort as the smallest value. A placement against that order on the first
  sort reads as two queries — the null rows and the rest — concatenated, the window carried across
  them; on a later sort, and on continuation paging or streaming, it is refused.
- **Pages without a total** are `OFFSET … LIMIT`; **distinct values** are `SELECT DISTINCT VALUE`.

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
