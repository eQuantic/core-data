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
| Includes | server-side `$lookup` joins (reference and collection navigations); a shaped include runs its `$match`/`$sort`/`$skip`/`$limit` inside a `$lookup` pipeline (MongoDB 5.0+); every join reads the collection the unit of work keeps the related type in, an override of `CollectionName<TEntity>()` included |
| Read shapes | relation counts as `$lookup` + `$count`, nulls placed against MongoDB's order with an is-null flag, `$skip`/`$limit` pages without a count, distinct values via `$group` — see [Querying](../querying/includes.md) |
| Filters across relations | the related keys resolved first — one aggregation per test over the related collection (`$match`, `$group` on the key), inside the open transaction's session — then `$in`/`$nin`; past 10,000 keys or 1 MiB of them, a `$lookup` in the read's own aggregation, and a set-based write runs over the matching ids in batches — see [below](#filters-across-relations-in-or-lookup) and [Relations and text in filters](../querying/predicates.md) |
| Text ignoring case | an escaped, anchored, case-insensitive regular expression (`/^…\z/i`); `ToLower() == v` keeps C#'s meaning (a value with an upper-case letter matches nothing) |
| Set-based writes | native operators: `$set`, `$inc`, `$mul`, `$push`, `$addToSet`, `$pull` |
| Concurrency | `[ConcurrencyToken]` → version-filtered conditional replaces/deletes |
| Named locks | **leases** in `_locks`: one conditional `findOneAndUpdate` upsert on the server's clock (`$$NOW`), acknowledged by a majority of the replica set, renewed while held, swept by a TTL index; a dead holder's lease expires — see [Named locks](../writing/locks.md) |
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
  back equal, and `null` stays `null`. Names starting with `$` or holding a `.` (a JSON Schema's
  `$schema`, `$id`, `$ref`) are stored as they are by every write — an insert, a replace, a modify of
  the members named, an upsert, a set-based update — on MongoDB 5.0 or later; an earlier server
  refuses such names on any write, an insert included.
- **Sequence** — an atomic counter: one document per `<collection>.<element>` in `_sequences`,
  advanced by `findOneAndUpdate` with `$inc` (upserted on first use), one `$inc` per counter for a
  whole commit, acknowledged by a majority of the replica set (a `w:1` acknowledgement can be rolled
  back by a failover, and the number handed out again). No number is ever handed out twice. The
  counter advances **outside** the open transaction, as a PostgreSQL sequence's `nextval` does:
  transactions inserting into one collection at once never wait on, or conflict over, its counter,
  and a transaction that rolls back leaves the numbers it took unused — a gap, as on PostgreSQL. A
  commit that fails before it writes (the clock or a counter could not be read) keeps its inserts
  staged, and the retried commit gives them their values again; a counter advanced before the
  failure leaves a gap, never a repeat.
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
An entity whose id is unset is a new document: it takes the id the driver generates on insert (an
`ObjectId`, a string represented as one, a `Guid`) before the upsert filters by it, or inserts it on
another target — never an empty `ObjectId` or a null `_id` that the next new entity would be merged
into.
`InsertManyAsync(…, skipDuplicates: true)` is an unordered `insertMany` discarding only E11000 — except
inside a transaction, which MongoDB aborts on any write error: there the `_id`s and every unique
index's values are looked up inside the session first, and only the free documents are inserted (a
concurrent writer taking a key in between surfaces as MongoDB's transient write conflict).
`UpdateAndGetAsync` runs `findOneAndUpdate` per matching document. E11000 — from a write, a bulk write
or a command — is `DuplicateKeyException` naming the index. See
[Upserts and write outcomes](../writing/upserts.md).

## Filters across relations: `$in` or `$lookup`

A filter that crosses a relation (`x.Mentions.Any(m => m.UserId == u)`, `x.Repository.WorkspaceId == w`,
`x.Container == null`) first reads the distinct keys of the related documents that meet it — one
aggregation, inside the open transaction's session, through the asynchronous API with the read's
cancellation token (the synchronous reads use the synchronous API). Which form the test then takes
depends on how many keys that read finds:

- **Up to 10,000 keys and 1 MiB of them**: an `$in` over them (`$nin` negated) in the read's
  `$match`, so the owner's key is matched through its own index.
- **Past either bound**: the key read stops there (the keys are never all held), and the test runs
  in the read's own aggregation instead — a `$lookup` of one related document meeting the condition
  (and its type's global filter), correlated by the owner's key and the related key, whose presence
  is the test's answer (MongoDB 5.0 or later). An `$in` past 16 MB would not even reach the server;
  the `$lookup` has no such bound, and costs a lookup per owner document that reaches it — an index
  on the related key (`x.ItemId` for a collection) keeps each to one seek.
- **A set-based write past the bound** (`UpdateManyAsync`, `DeleteManyAsync`, `UpdateAndGetAsync`):
  a write's filter cannot hold a `$lookup`, so one aggregation that holds it reads the ids of the
  documents the filter matches, with each relation's answer for each, and the write runs over them in
  batches of 10,000 ids or 1 MiB, each filtered on its ids and on the rest of the filter again. The
  relations are taken as they held when they were read, as the keys of the `$in` form are.

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
