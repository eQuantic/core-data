# The lean write model

eQuantic.Core.Data does **not** track changes. There is no snapshotting, no proxy generation, no
change detection pass, no hidden identity map. The write model is built on a different bargain:

> You tell the repository what you intend (`Add`, `Modify`, `Merge`, `Remove`); the unit of work
> stages exactly that; `Commit` flushes it as the store's best native batch.

## Why no change tracking

Change tracking buys "just mutate the object and call SaveChanges" at real costs: per-entity
snapshots, a detection pass proportional to everything loaded, semantics that depend on invisible
tracker state, and bugs of the "why did this save?" family. In services built on CQRS-style
explicit commands — the architecture these libraries serve — the caller *already knows* whether it
is creating, updating or deleting. The engine takes that knowledge instead of reconstructing it.

What you give up: automatic dirty detection, lazy loading. What you get: predictable writes you can
read off the code, a flush that costs O(staged writes) instead of O(loaded entities), and entities
that are plain objects — safe to serialize, cache and pass around.

## The staging verbs

| Verb | Meaning | Relational | MongoDB | Cosmos DB | Cassandra |
|---|---|---|---|---|---|
| `Add` | this is new | `INSERT` | `InsertOne` | create/upsert item | `INSERT` (upsert) |
| `Modify` | this exists, persist its current state | full-row `UPDATE` by key | `ReplaceOne` by `_id` | replace/upsert item | `INSERT` (upsert) |
| `Merge(persisted, current)` | apply `current`'s values | `UPDATE` | `ReplaceOne` (upsert) | upsert | `INSERT` (upsert) |
| `Remove` | delete it | `DELETE` by key | `DeleteOne` | delete item | `DELETE` by primary key |

Declared [concurrency tokens](../writing/concurrency.md) refine these into conditional writes, and
[lifecycle conventions](../writing/lifecycle.md) turn `Remove` into a soft delete where declared.
On upsert-native stores (Cassandra; Cosmos without a token) `Add` and `Modify` genuinely converge —
the table documents reality, not a pretty abstraction.

## Commit — one native batch

```csharp
await repo.AddAsync(a);
await repo.ModifyAsync(b);
await otherRepo.RemoveAsync(c);
var affected = await unitOfWork.CommitAsync();   // 3
```

- **Relational**: one `DbBatch`, inside a transaction — the commit is **atomic**; identity keys
  come back (`RETURNING` / `OUTPUT INSERTED`) and are assigned onto the entities; concurrency
  violations roll the whole flush back.
- **MongoDB**: one ordered `BulkWrite` per collection.
- **Cosmos DB**: batched point writes with bulk execution enabled.
- **Cassandra**: statements prepared once per session, bound and executed concurrently.

`RollbackChanges()` discards everything staged and not yet flushed. Set-based writes
(`UpdateManyAsync`, `DeleteManyAsync`) are the deliberate exception: they run **immediately**,
server-side, without loading entities — that is their reason to exist.

## Explicit transactions

Atomicity beyond a single relational commit is opt-in and store-shaped:

| Store | `BeginTransactionAsync` gives you |
|---|---|
| Relational | a real transaction spanning multiple commits, with read-your-writes |
| MongoDB | a multi-document session (requires a replica set); reads inside see its writes |
| Cosmos DB | a `TransactionalBatch` — **single partition only**, and the provider says so |
| Cassandra | one atomic `LOGGED BATCH` on commit (no isolation — atomicity only, as Cassandra defines it) |

Each provider documents what its transaction *cannot* do instead of pretending: a Cassandra LWT
write refuses to join a `LOGGED BATCH`; a Cosmos batch crossing partitions throws with guidance.

## Where the stamps happen

Staging is also where cross-cutting write concerns run, so they apply on every provider uniformly:
`CreatedAt`/`UpdatedAt` stamps, `CreatedById`/`UpdatedById` attribution, soft-delete conversion and
concurrency-token bumps all happen when the write is staged — see
[Lifecycle](../writing/lifecycle.md) and [Optimistic concurrency](../writing/concurrency.md).
