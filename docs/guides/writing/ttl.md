# Time to live

`[TimeToLive]` declares that an entity's data **expires**. Three stores have native expiry, with
genuinely different semantics — the engine maps the declaration to each native mechanism and keeps
the differences documented instead of papering over them.

```csharp
[Entity("sessions")]
[TimeToLive(3600)]                    // seconds
public sealed class Session : IEntity<string>, IEntityTimeMark
{
    public string Id { get; set; } = "";
    public DateTime CreatedAt { get; set; }     // MongoDB's expiry counts from this
    public string Payload { get; set; } = "";
    public string GetKey() => Id;
    public void SetKey(string key) => Id = key;
}
```

## What each store does with it

| Store | Mechanism | Expiry counts from | Materialized by |
|---|---|---|---|
| Cosmos DB | container `DefaultTimeToLive` | the item's **last write** | `EnsureCollection()` |
| Cassandra | table `default_time_to_live` | the row's **last write** | `EnsureCollection()` |
| MongoDB | a **TTL index** (`expireAfterSeconds`) | the **date member's value**, per document | `EnsureCollection()` |
| Relational | — no native TTL; the annotation is ignored (schedule deletes yourself) | | |

The MongoDB nuance is the important one: Mongo expires *per document, relative to a date field*.
The engine resolves that date member as the lifecycle `CreatedAt` (`IEntityTimeMark`) by
convention, or an explicit member through the fluent model:

```csharp
model.Entity<Session>(entity => entity
    .TimeToLive(x => x.LastSeenAt, TimeSpan.FromHours(1)));   // sliding expiry: refresh LastSeenAt on writes
```

`[TimeToLive]` on a MongoDB entity with **no** date member throws at model build with exactly that
guidance — the store has the concept, so an incomplete declaration is an error, not a shrug.

## Per-write TTL (Cassandra)

Cassandra can also expire *individual writes*, overriding the table default:

```csharp
await uow.CommitAsync(o => o.WithTtl(TimeSpan.FromMinutes(5)));   // this flush's inserts expire in 5m
```

## Checking what you declared

`Explain()` reports the TTL on every model, in the store's own terms — container-level, table
`default_time_to_live`, or `documents expire 3600s after CreatedAt (per-document TTL index)` — so
the semantic difference is always one read away. See [Explain](../modeling/explain.md).
