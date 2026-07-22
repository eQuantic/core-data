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
| Cosmos DB | a `TransactionalBatch` | **single partition only** — crossing partitions throws with guidance |
| Cassandra | writes deferred and flushed as one atomic `LOGGED BATCH` | atomicity only (no isolation — Cassandra's own definition); a [concurrency-token](concurrency.md) write refuses to join the batch |

## Set-based writes are outside both

`UpdateManyAsync` / `DeleteManyAsync` run **immediately**, server-side, without staging:

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
