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
| Cosmos DB | replace with `If-Match: <etag>` (the token is a `string` member carrying `_etag`) | HTTP 412 → conflict |
| Cassandra | a **lightweight transaction**: `UPDATE … IF version = @old` (new rows: `INSERT … IF NOT EXISTS`) | `[applied] = false` → conflict |
| MongoDB | `ReplaceOne` filtered on `{_id, version}` with the bump in the document | matched 0 → conflict |

Deletes are protected the same way (`DELETE … WHERE/IF version = @old`), so removing an entity
someone else already changed is also a conflict — deliberately.

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
