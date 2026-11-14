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

**An enum stored as its labels** (`[EnumType]`, or `Enum<TEnum>()` on the model) aggregates by its
members' values, as C# does, never by the labels' text. `MinAsync(t => t.Priority)` answers the member
with the smallest value (`MinAsync(t => (int)t.Priority)` its number), and nulls are skipped:

| Store | How a minimum or a maximum over labels runs |
|---|---|
| Relational | `MIN(CASE col WHEN 'low' THEN 1 … END)`, the value mapped back to its member |
| MongoDB · Cosmos DB | the distinct members present under the filter (at most the enum's members), the extreme picked by value |
| Cassandra | the column read under the read's gates, the extreme picked by value |

A sum or an average over such a member is **refused** on every store with the reason: a label has no
arithmetic, and the store would add the text (or, on MySQL, a label's position), never the members'
values. Read the members and add their values, or store the enum as its number.

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

A key stored as an enum's labels groups by member, comes back as the member, and a `HAVING` on it
compares by the members' values (`g => g.Key >= Priority.High` is membership in the labels whose member
satisfies it). An aggregate over such a member in a group the store computes (`g.Max(t => t.Priority)`,
a sum, an average) is refused with the reason, since the store would compare or add the labels' text;
ask `MinAsync`/`MaxAsync` with the group's filter, or group the rows client-side. A group that already
runs client-side (a residual filter, behind `.AllowClientEvaluation()`) aggregates the members themselves.

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

## Distinct values

Each distinct value of a member — or each distinct combination of several — among the rows a
filter matches, deduplicated by the store and never by loading the rows whole:

```csharp
var distinct = (IDistinctReadRepository<Comment>)repo;
var issues = await distinct.GetDistinctAsync(
    c => new { c.Repository, c.IssueNumber },          // one member, or several
    c => c.Repository == "repo-a");                    // the rows to look at (optional)
// (repo-a, 1), (repo-a, 2) — each combination once
```

The values come back in no particular order; an order, a skip or a limit in the options is refused
rather than ignored — order the (already small) result in memory. The query filters apply as on any
read.

| Store | Executes as |
|---|---|
| Relational | `SELECT DISTINCT a, b FROM … WHERE …` |
| MongoDB | `$match` then a `$group` on the selected value (the driver's LINQ `Distinct`) |
| Cosmos DB | `SELECT DISTINCT VALUE { … } FROM … WHERE …` |
| Cassandra | `SELECT a, b … GROUP BY a, b` when the members are a prefix of the primary key that covers the partition key; any other selection fetches the selected columns and deduplicates them client-side, behind `.AllowClientEvaluation()` |

A filter with a client-side residual (behind the store's opt-in) fetches the selected and the
residual's columns and deduplicates in memory — the same gate as any read.
