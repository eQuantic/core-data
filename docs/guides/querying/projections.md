# Projections

`GetMappedAsync` (and `GetFirstMappedAsync`, `GetPagedAsync` with a map) projects entities into any
shape — and the engine fetches **only what the projection needs**.

```csharp
// a DTO, an anonymous type, a scalar — any selector:
var rows = await repo.GetMappedAsync(
    p => new ProductRow(p.Id, p.Name, p.Price),
    new QueryOptions<Product>().Where(p => p.Category == "Peripherals").OrderBy(p => p.Name));

var names = await repo.GetMappedAsync(p => p.Name, options);
```

## What actually happens

The engine analyzes the selector for the members it reads and narrows the fetch:

- **Relational** — the `SELECT` lists only the referenced columns (plus anything a residual filter
  needs); materialization fills just those members.
- **Cassandra** — same: a projected read selects only the referenced columns; on OR-split queries
  the primary key is added so de-duplication stays correct.
- **Cosmos DB / MongoDB** — the selector flows into the native query pipeline (`SELECT VALUE` /
  `$project`) through the store's LINQ provider.

A selector that needs the whole entity (it passes `p` somewhere, calls a method on it) is detected
and the fetch stays full — correctness first, narrowing when provable.

## Renames and conversions apply

Projections resolve through the same model as everything else: a `[StoredAs]` member projects from
its stored name, a `Converts(...)` member materializes through its converter. There is no separate
"projection path" to fall out of sync.

## When you need more than member-shuffling

Computed projections (string concatenation, arithmetic) execute where the store's LINQ provider can
translate them (Cosmos, MongoDB) and client-side over the fetched columns elsewhere — the fetched
columns are still narrowed to what the computation reads. For aggregate shapes
(`Count`/`Sum` per group), use [GROUP BY](aggregates.md), which pushes the aggregation itself down.
