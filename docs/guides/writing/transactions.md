# Commits and transactions

Two levels of atomicity exist, and the engine keeps them distinct on purpose: the **commit** (what
`CommitAsync` flushes) and the **explicit transaction** (`BeginTransactionAsync` …
`CommitTransactionAsync`), which groups multiple commits where the store can.

## The commit

```csharp
await repo.AddAsync(a);
await repo.ModifyAsync(b);
var affected = await unitOfWork.CommitAsync();
```

| Store | One commit is… | Atomic? |
|---|---|---|
| Relational | one `DbBatch` inside a transaction; identity keys read back (`RETURNING` / `OUTPUT INSERTED`) | **yes** |
| MongoDB | one ordered `BulkWrite` per collection | per collection |
| Cosmos DB | batched point writes (bulk execution) | per item |
| Cassandra | prepared statements executed concurrently | per statement |

The table is the honesty: a relational commit is a transaction; a Cosmos commit is not, because
Cosmos itself offers atomicity only inside one partition. The provider never fakes a guarantee the
store cannot give.

Commit-time options are per-store extensions on `Commit(Action<SaveOptions>)` — e.g. Cassandra's
`o => o.WithConsistency(ConsistencyLevel.Quorum)` and `o => o.WithTtl(TimeSpan.FromHours(1))`.

## Explicit transactions

```csharp
await unitOfWork.BeginTransactionAsync();
try
{
    await repo.AddAsync(order);
    await unitOfWork.CommitAsync();          // inside the transaction

    var fresh = await repo.GetAsync(order.Id);   // read-your-writes (relational, MongoDB)

    await repo.ModifyAsync(fresh!);
    await unitOfWork.CommitAsync();
    await unitOfWork.CommitTransactionAsync();
}
catch
{
    await unitOfWork.RollbackTransactionAsync();
    throw;
}
```

| Store | `BeginTransactionAsync` gives you | Watch for |
|---|---|---|
| Relational | a real DB transaction spanning commits, read-your-writes | — |
| MongoDB | a multi-document session; reads inside see its writes | requires a replica set |
| Cosmos DB | a `TransactionalBatch` | **single partition only, at most 100 operations** — crossing partitions or passing the limit throws with guidance before anything is written, and discards the transaction's writes |
| Cassandra | writes deferred and flushed as one atomic `LOGGED BATCH` | atomicity only (no isolation — Cassandra's own definition); a [concurrency-token](concurrency.md) write refuses to join the batch |

## Set-based writes are outside both

`UpdateManyAsync` / `DeleteManyAsync` run **immediately**, server-side, without staging (joining an
open explicit transaction on the relational stores and MongoDB):

```csharp
long touched = await repo.UpdateManyAsync(
    p => p.Category == "Peripherals",
    p => new Product { Price = p.Price * 0.9m, Quantity = p.Quantity + 1 });
```

The update factory renders natively — SQL `SET price = price * 0.9`, Mongo `$mul`/`$inc`/`$set`/
`$push`/`$pull`, Cassandra collection add/remove and counter increments, Cosmos patch operations.
Each provider renders what its store can and refuses what it cannot (Cassandra: multiplication has
no CQL form and says so; counters only move by increments and the message explains the pattern).
Soft-delete entities turn `DeleteManyAsync` into a set-based stamp — see
[Lifecycle](lifecycle.md).

### What a set-based write counts, and when it applies

The number a set-based write returns is the rows its filter **matched** — whether or not the update
changed their values: an update that writes a row's own value back still matched it. And each row is
written only **while it still matches the filter**: a row another writer changed in between is
evaluated again, so a filter on the key plus other members is a compare-and-set on every engine:

```csharp
// Claim a queued job: of two workers running this at once, one gets 1 and the other 0.
var claimed = await repo.UpdateManyAsync(
    x => x.Id == id && x.Status == "queued",
    x => new Job { Status = "running", Owner = me });
```

| Store | Each row is written | The count is |
|---|---|---|
| Relational | by one `UPDATE … WHERE` / `DELETE … WHERE`, which the store re-evaluates on a row another transaction changed | the rows matched (MySQL and MariaDB: the data source always reports found rows, `UseAffectedRows=false`) |
| MongoDB | by one `updateMany`/`deleteMany`; a write conflict makes the server evaluate the filter again | `MatchedCount` |
| Cosmos DB | per matching item, at most 32 at once: a patch under the filter (`PatchItemRequestOptions.FilterPredicate`), a delete under the `_etag` of a read that still matched | the patches and deletes applied |
| Cassandra | a filter on the primary key alone: one `UPDATE`/`DELETE` (Cassandra's `UPDATE` is an upsert, so a row deleted in between is written again); a filter on other members too: a **lightweight transaction** per row, the other members its `IF` | the rows found / the transactions applied |

Cosmos DB takes at most ten paths in one patch. An update that needs more (the update's lifecycle
stamps count) patches each item several times in **one transactional batch** of its partition: the
first patch carries the filter (or the `_etag`), the batch's isolation keeps other writers out until
the last patch has applied, and a condition that fails leaves the item untouched. A member-scoped
`ModifyAsync` of more than ten members is staged the same way, and inside an explicit transaction its
patches join the transaction's batch.

A filter may cross a relation (`DeleteManyAsync(x => x.Repository.WorkspaceId == w)`): the relational
stores put a correlated `EXISTS` in the `UPDATE`/`DELETE` itself; MongoDB, Cosmos DB and Cassandra
read the related keys first — MongoDB inside the open transaction's session — and write the rows
those keys find (MongoDB, past what an `$in` carries, the ids one aggregation with a `$lookup` finds,
in batches), as [Relations and text in filters](../querying/predicates.md) describes.

On Cassandra a conditional write needs the partition key in its filter (the rows are read by their
key first), takes no condition on a counter table, and costs a Paxos round per row; an increment of a
plain column, which CQL cannot apply in place, is written the same way, under `IF column = value
read`.

`UpdateAndGetAsync` applies an update the same way and returns the rows it wrote; `UpsertAsync` and
`InsertManyAsync` run at once too — see [Upserts and write outcomes](upserts.md). On Cosmos DB and
Cassandra an explicit transaction defers the *staged* writes into one batch; an immediate write cannot
join that batch (it has to read, or to be conditioned on what it reads), and runs on its own.
