# MongoDB — deep dive

The most complete document-store provider: the official driver underneath, the full contract
surface on top, and the model (class maps, renames, converters) feeding **both** documents and
queries so they can never disagree.

## Capabilities at a glance

| Area | Support |
|---|---|
| Writes | staged → one ordered `BulkWrite` per collection; multi-document transactions on replica sets |
| Queries | filters/sorts/projections through the driver's LINQ over the class maps |
| Aggregates | `$group` pushdown for scalar aggregates and typed `GroupByAsync` (+ HAVING as `$match`) |
| UNION | one aggregation with **`$unionWith`** per extra branch; `Distinct` via `$group` |
| Includes | server-side `$lookup` joins (reference and collection navigations) |
| Set-based writes | native operators: `$set`, `$inc`, `$mul`, `$push`, `$addToSet`, `$pull` |
| Concurrency | `[ConcurrencyToken]` → version-filtered conditional replaces/deletes |
| TTL | `[TimeToLive]` → per-document TTL index (from `CreatedAt` or an explicit date member) |
| Ordered reads | `[ClusteringKey]` → compound index with directions |
| Migrations | collections, all index kinds (unique, filtered, text, TTL, composite), field ops, `Run` |

## The model feeds the driver

`[Entity]`, `[EntityKey]`, `[StoredAs]`, `[Unmapped]` and the fluent
`Collection/Key/Field/Ignore/Converts` all land in the driver's **class maps** — which the driver's
LINQ provider reads for every translation. Consequences worth internalizing:

- A `[StoredAs("n")]` member filters as `{ "n": … }` — renames are end-to-end.
- A `Converts(x => x.Grade, …)` member compares filter constants **through the converter** — an
  enum stored as `"premium"` matches `x.Grade == Grade.Premium`.
- `[EntityKey]` maps the member to `_id`; point lookups (`GetAsync`) resolve through the class map.

## Set-based updates, natively

```csharp
await repo.UpdateManyAsync(
    p => p.Category == "Peripherals",
    p => new Product
    {
        Price = p.Price * 0.9m,                    // $mul
        Quantity = p.Quantity + 1,                 // $inc
        Tags = p.Tags.Add("sale"),                 // $push  (.AddUnique → $addToSet)
        Flags = p.Flags.Remove("new"),             // $pull
    });
```

The update factory renders as one native `UpdateMany` — no documents load, no round-trips per
entity.

## Embedded vs referenced

MongoDB's idiom is embedding, and the engine respects it: embedded objects and collections
serialize with the document, filter with dotted paths, and need no `Include`. `$lookup` includes
exist for genuinely referenced designs. Choose per aggregate, not per framework limitation.

## Sessions and transactions

`BeginTransactionAsync` opens a session (replica set required); staged writes flush inside it and
reads through the repositories see the session's writes. Without a transaction, each `Commit` is
an ordered bulk write per collection — per-document atomicity, the store's own guarantee.

## Registration recap

```csharp
services.AddMongoRepositories(connectionString, "shop", model => { /* fluent model */ });
services.AddMongoMigrations(assemblies);
```
