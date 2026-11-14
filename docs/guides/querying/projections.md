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

## Counting a relation

A projection can count a collection navigation — all of it, or the entities a filter keeps — and
the store counts it: the related entities are **never loaded**, so a list of packages can show how
many live versions each has in one read.

```csharp
var rows = await packages.GetMappedAsync(p => new
{
    p.Id,
    p.Name,
    Versions = p.Versions.Count(v => v.DeletedAt == null),   // filtered
    Total = p.Versions.Count(),                              // or Count / LongCount(...)
}, new QueryOptions<Package>().Where(p => p.OwnerId == owner));
```

`Count()`, `Count(filter)`, `LongCount(…)`, the collection's `Count` property and an array's
`Length` (or `LongLength`) all count; the
filter may read captured values. A member counts as a relation when its elements are entities with
a foreign key back to the parent (the same resolution [includes](includes.md) use), so a count over
a member the document embeds (`p.Tags.Count`) stays the store's ordinary projection.

| Store | A relation count is | Cost |
|---|---|---|
| Relational | a correlated `(SELECT COUNT(*) FROM versions v WHERE v.package_id = packages.id AND …)` in the select list | one subquery per count, per row, inside the one statement |
| MongoDB | a `$lookup` whose pipeline matches the related documents and ends in `$count`, beside the document into the `$project` | one lookup stage per count; only the number travels |
| Cosmos DB | one grouped query per count over the related container: `SELECT fk, COUNT(1) … WHERE fk IN (…) AND … GROUP BY fk` | the parents are read whole and the map runs in memory over them and their counts |
| Cassandra | `SELECT fk, COUNT(*) … GROUP BY fk` when the related table is partitioned by the foreign key and the filter pushes down; otherwise the foreign keys (and the columns a client-side filter reads) are fetched and counted client-side, behind `.AllowClientEvaluation()` | a null test (`v.DeletedAt == null`) has no CQL form, so such a count runs client-side |

A count's filter may test the counted entity's own relations, as any filter does
([relations in filters](predicates.md#filters-across-relations)): `r.Steps.Count(s => s.Logs.Any())`,
`r.Steps.Count(s => s.Runner.Name == name)`, negated, `s.Runner == null`, or combined under `&&`/`||`.
Every store counts with them, resolved as a filter's are: a correlated `EXISTS` inside the count's
subquery on the relational engines; the related keys read first and an `$in`/`IN` over them in the
count on MongoDB (a `$lookup` inside the count's past the bound), Cosmos DB and Cassandra (under the
read's opt-ins, so a related read that names no partition needs `.AllowFiltering()`, and an `IN` the
counted table cannot take runs client-side behind `.AllowClientEvaluation()`). No store answers a
count that ignored a relation, or reads it as a member the document embeds.

A read with relation counts refuses `Include` (a mapped read never loads navigations): count them,
or include them in an entity read — not both.

## When you need more than member-shuffling

Computed projections (string concatenation, arithmetic) execute where the store's LINQ provider can
translate them (Cosmos, MongoDB) and client-side over the fetched columns elsewhere — the fetched
columns are still narrowed to what the computation reads. For aggregate shapes
(`Count`/`Sum` per group), use [GROUP BY](aggregates.md), which pushes the aggregation itself down.
