# Optimistic concurrency

One declaration, one exception, four native mechanisms. Declare a token on the model and every
write on that entity means: **a stale write must fail — never silently win**.

```csharp
public sealed class Ledger : IEntity<Guid>
{
    public Guid Id { get; set; }
    public decimal Balance { get; set; }
    [ConcurrencyToken] public long Version { get; set; }   // int, long — or string on Cosmos (the _etag)
    public Guid GetKey() => Id;
    public void SetKey(Guid key) => Id = key;
}
```

```csharp
var first  = await repo.GetAsync(id);    // Version == 3
var second = await repo.GetAsync(id);    // Version == 3 — a concurrent reader

first!.Balance = 150m;
await repo.ModifyAsync(first);
await uow.CommitAsync();                 // wins; Version becomes 4 (on the entity too)

second!.Balance = 999m;
await repo.ModifyAsync(second);
await uow.CommitAsync();                 // throws ConcurrencyConflictException
```

`ConcurrencyConflictException` carries `Expected` and `Affected` and one instruction: reload,
reapply, retry. The stale writer's data **never reached the store**.

## The four mechanisms

| Store | A `Modify` becomes | A lost race is |
|---|---|---|
| Relational | `UPDATE … SET …, version = @next WHERE key = @k AND version = @old` | 0 rows affected → the whole flush **rolls back** |
| Cosmos DB | replace with `If-Match: <etag>` (the token is a `string` member carrying `_etag`) | HTTP 412, or 404 when the document was removed → conflict (a transactional batch: nothing applied) |
| Cassandra | a **lightweight transaction**: `UPDATE … IF version = @old` (new rows: `INSERT … IF NOT EXISTS`) | `[applied] = false` → conflict |
| MongoDB | `ReplaceOne` filtered on `{_id, version}` with the bump in the document | matched 0 → conflict |

Deletes are protected the same way (`DELETE … WHERE/IF version = @old`; on Cosmos DB a delete with
`If-Match: <etag>`, in a transactional batch too), so removing an entity someone else already changed,
or already removed, is also a conflict — deliberately.

## The costs, stated plainly

- **Relational / MongoDB / Cosmos** — effectively free; the condition rides the write it was
  already doing.
- **Cassandra** — an LWT is a **Paxos round**: significantly more expensive than a plain upsert.
  That is the price *Cassandra* charges for compare-and-set semantics; the token declaration is
  your explicit opt-in to pay it, per entity. Consequences the provider enforces honestly: a token
  write refuses to join a `LOGGED BATCH` (Cassandra restricts conditional batches), and entities
  without a token keep plain last-write-wins upserts — the default is the store's own semantics.

## Version bookkeeping

The engine owns the token: `0`/unset means never persisted (first write stamps `1`), every
successful write bumps it and writes the bump back onto the entity, so the instance you hold stays
current after `CommitAsync`. Never set the token yourself.

## Compare-and-set without a token

A token guards a row you read and then write back. When the decision is a single condition — "claim
this job if it is still queued", "take the next number" — a set-based write is the compare-and-set
itself, with no read beforehand and no token:

```csharp
var claimed = await repo.UpdateManyAsync(
    x => x.Id == id && x.Status == "queued",
    x => new Job { Status = "running", Owner = me });
if (claimed == 0) { /* someone else claimed it */ }

var next = (await ((IReturningUpdateRepository<ItemType, Guid>)repo).UpdateAndGetAsync(
    x => x.Id == typeId, x => new ItemType { NextSequence = x.NextSequence + 1 })).Single().NextSequence;
```

Every engine writes each row only while it still matches the filter and counts the rows it matched —
an `UPDATE … WHERE` on SQL, a filtered `updateMany` on MongoDB, a patch under a filter predicate on
Cosmos DB, a lightweight transaction's `IF` on Cassandra. See
[Commits and transactions](transactions.md#what-a-set-based-write-counts-and-when-it-applies).

## A taken key is not a conflict

`ConcurrencyConflictException` says *another writer changed this row*. A write the store refuses
because a key or a unique key is already taken is a different failure, and every engine reports it as
`DuplicateKeyException` (with the constraint or index name where the store gives one) — at commit, in
a bulk load, a set-based update, an upsert or `InsertManyAsync`. Upserts exist so that "insert or
update" never has to be a catch block — see [Upserts and write outcomes](upserts.md).

## Retry pattern

```csharp
for (var attempt = 0; ; attempt++)
{
    var entity = await repo.GetAsync(id) ?? throw new NotFoundException();
    Apply(entity, change);
    await repo.ModifyAsync(entity);
    try { await uow.CommitAsync(); break; }
    catch (ConcurrencyConflictException) when (attempt < 3) { /* loop: reload + reapply */ }
}
```

Reload-reapply-retry is the correct loop for business operations; the exception exists so **you**
decide the merge policy instead of the store deciding it for you (by overwriting).
