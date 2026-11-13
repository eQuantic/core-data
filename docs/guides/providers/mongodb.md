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

## Enums as labels, JSON members, store-generated members

The shared vocabulary (`[EnumType]`, JSON members, `[StoreGenerated]`) means here what it means on
every other provider, through MongoDB's own mechanisms:

- **Enums as labels** — `[EnumType]` on the enum, or `model.Enum<TEnum>()`, stores each member as
  its `[StoredAs]` label (or its name) as a BSON string. It is the member's serializer, so the
  LINQ pipeline, the filter of a set-based write and its `$set` all compare and write labels —
  `x.State == State.InProgress` (which the compiler turns into `(int)x.State == 1`) included. An
  enum declared neither way keeps its number. The driver's serializers are global and fixed on
  first use: declare `Enum<TEnum>()` before the first read or write (a late declaration refuses).
- **JSON members** — `JsonDocument`, `JsonElement` and `JsonNode` are embedded as the BSON value
  their JSON is: an object as a document, an array as an array, a scalar as the scalar. They come
  back equal, and `null` stays `null`.
- **Sequence** — an atomic counter: one document per `<collection>.<element>` in `_sequences`,
  advanced by `findOneAndUpdate` with `$inc` (upserted on first use), one `$inc` per counter for a
  whole commit, inside the open transaction. No number is ever handed out twice.
- **Clock** — the server's clock (`hello`'s `localTime`), read once per commit and given to every
  insert of it, the way PostgreSQL's `now()` is the transaction's time.
- **Updates keep them** — a staged replace becomes one pipeline update (`$replaceWith` over
  `$mergeObjects` of the entity and the stored elements), so it never writes a store-generated
  element (MongoDB 4.2 or later). A set-based update writes only what it names.

```csharp
model.Enum<Channel>()
     .Entity<Receipt>(entity => entity
         .Generated(x => x.Number)                          // Sequence: inferred from an integral member
         .Generated(x => x.IssuedAt, StoreValue.Clock));    // Clock
```

`Explain()` names the labels, the embedded JSON and the store-generated members.

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

`UpdateManyAsync` returns the documents the filter **matched** (it used to return the ones modified,
so a compare-and-set that wrote the same value read as a lost race), and goes through the set like
every other provider's: a soft-delete entity's `DeleteManyAsync` stamps `DeletedAt`, and an update
stamps `UpdatedAt`.

## Upserts and write outcomes

`UpsertAsync` is `findOneAndUpdate` of the target and, when nothing matches, an upsert of
`$setOnInsert` (the update runs again when that finds the target after all). With a unique index on
the target, ten concurrent upserts leave one document; without one, MongoDB itself can insert twice.
`InsertManyAsync(…, skipDuplicates: true)` is an unordered `insertMany` discarding only E11000 — except
inside a transaction, which MongoDB aborts on any write error, so there a duplicate fails the call.
`UpdateAndGetAsync` runs `findOneAndUpdate` per matching document. E11000 — from a write, a bulk write
or a command — is `DuplicateKeyException` naming the index. See
[Upserts and write outcomes](../writing/upserts.md).

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
