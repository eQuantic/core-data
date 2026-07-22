# Aggregates and GROUP BY

## Scalar aggregates

`Count`, `Any`, `Sum`, `Min`, `Max`, `Average` — all filtered by the same `QueryOptions`, all
pushed down where the store can:

```csharp
long total    = await repo.CountAsync(inCategory);
bool any      = await repo.AnyAsync(inCategory);
int  units    = await repo.SumAsync(p => p.Quantity, inCategory);
var  cheapest = await aggregates.MinAsync(p => p.Price, inCategory);
double mean   = await aggregates.AverageAsync(p => p.Quantity, inCategory);
```

- **Relational** — `COUNT/SUM/MIN/MAX/AVG` in SQL, always.
- **MongoDB** — `$group` accumulators in one aggregation.
- **Cosmos DB** — `VALUE COUNT/SUM/MIN/MAX/AVG` (single-partition when the filter pins one).
- **Cassandra** — native aggregates when the whole filter pushed down; a residual filter degrades
  to fetching the matching rows and aggregating client-side — behind the same
  [gates](pushdown.md) as any read, never silently.

A computed selector (`p => p.Price * p.Quantity`) aggregates client-side over a narrowed fetch —
correct first, pushed when provable.

## Typed GROUP BY

One shape on every provider — key selector, result selector over `IGrouping`, optional `HAVING`:

```csharp
var byCategory = await repo.GroupByAsync(
    p => p.Category,                                       // the key (member or composite: new { p.A, p.B })
    g => new CategoryStat(g.Key, g.Count(), g.Sum(p => p.Quantity), g.Max(p => p.Price)),
    having: g => g.Count() > 10,                           // optional
    options);
```

The result selector supports `g.Key`, `g.Count()`, `g.Sum/Min/Max/Average(selector)` — the shapes
every store can aggregate. The interpreter validates the shape **first**, so the contract (what is
accepted, what is rejected, with which message) is identical across providers.

Per store:

| Store | Execution |
|---|---|
| Relational | `GROUP BY` + aggregates + `HAVING`, all in SQL |
| MongoDB | one `$group` pipeline (+ `$match` after, for `HAVING`) |
| Cassandra | native CQL `GROUP BY` — restricted, as Cassandra requires, to the **primary key prefix** (full partition key, optionally followed by clustering columns in order); CQL has no `HAVING`, so the predicate's aggregates are computed on the cluster as extra select cells and groups filter as they stream back — no extra rows travel |
| Cosmos DB | **honestly rejected**: the SDK's LINQ emits `SELECT VALUE {…}`, which Cosmos SQL cannot combine with `GROUP BY`. The exception says exactly that and points to the alternatives (group client-side over a filtered read, or use a provider whose GROUP BY pushes down) |

The Cosmos rejection is the philosophy in action: the provider *could* fetch everything and group
in memory silently — it refuses to lie about where the work happens. (A hand-built Cosmos SQL
`GROUP BY` pushdown is on the roadmap; the shape contract already validates so code written today
keeps working when it lands.)

## HAVING semantics

`having` receives the same `IGrouping` shape and may reference the projected aggregates or others —
aggregates the projection didn't select are still computed server-side where the store allows.
`Sorting does not apply to a grouped read` (every provider says so consistently): order the grouped
result in memory — it is already small.
