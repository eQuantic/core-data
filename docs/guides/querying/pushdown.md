# Pushdown and the honest gates

This page is the heart of the engine. Understand it and every provider's behavior becomes
predictable — including the exceptions.

## The problem every ORM has and few admit

You write a filter. The store can express *some* of it. What happens to the rest?

The industry's classic answers: silently evaluate it client-side (old EF — queries that "worked"
by downloading tables), throw a generic error, or a mix nobody can predict. All three break the
same contract: **the code stops telling you what actually executes**.

## The pipeline

Every filter — a lambda, a specification, a wire-format filter — goes through the same steps:

```
Expression tree
   → FilterInterpreter (store-agnostic): normalize, partially evaluate captured variables,
     produce a QueryFilter IR (comparisons, IN, ranges, string ops, logical nodes…)
   → the provider's renderer: translate every node it can into native form
     (SQL WHERE / CQL / Mongo filter / Cosmos SQL)
   → what cannot render becomes the RESIDUAL
```

Then the gates apply:

1. **Everything renders** → the query runs fully server-side. Nothing to decide.
2. **Something is residual** → the engine refuses, with the untranslatable clause named, unless the
   call opts in with `.AllowClientEvaluation()`. With the opt-in, the pushed-down part still runs
   server-side; only the residual runs in memory **over the already-filtered rows** — and paging
   (`LIMIT`) is applied *after* the residual so results are never silently truncated.
3. **A clause renders but as a scan** (Cassandra: filtering outside the key columns) → a separate
   opt-in, `.AllowFiltering()`, acknowledges the scan.

```csharp
// Cassandra, the strictest store — both gates visible:
var rows = await repo.GetFilteredAsync(
    r => r.SensorId == 7 && ComplexBusinessRule(r),      // second clause can't render
    new QueryOptions<Reading>()
        .AllowClientEvaluation());                       // "yes, run the rest in memory over partition 7"
```

The point is not to make you type opt-ins — it is that **cost never happens without a visible
decision at the call site**, and code review can see it.

## What refusal looks like

Refusals are teaching moments, not shrugs. A few real messages:

> `The clause(s) 'x => ComplexBusinessRule(r)' cannot be expressed in CQL; call
> .AllowClientEvaluation() to run them client-side over the pushed-down rows, or restructure the
> filter around the partition/clustering keys.`

> `'Description' has no search index; declare one with SearchIndex(x => x.Description) to push
> LIKE down, or run the match client-side.`

> `Cassandra CQL has no '<>' operator; 'Status <> ?' is not expressible.`

Every `NotSupportedException` names the construct, states the store's reason, and offers the way
out. This is deliberate API design: the exception *is* the documentation at the moment you need it.

## Principled mechanisms instead of rejection

Refusing is the last resort. Where a store has a *native* mechanism that honors the semantics, the
engine uses it — that is the difference between this engine and a wrapper:

| You wrote | Naive translation fails because… | The engine does |
|---|---|---|
| `r.SensorId > 100` (Cassandra partition key) | CQL forbids ranges on partition keys | `token(sensor_id) > token(?)` — a token-range scan, the native form |
| `a == 1 \|\| b == 2` (Cassandra) | CQL has no OR across columns | **OR query-splitting**: one native query per branch, in parallel, merged and de-duplicated by primary key client-side — with per-branch `LIMIT` keeping top-N correct |
| `x.Region == region` from a variable (Cosmos) | — | partial evaluation resolves the captured value, and the query **pins the partition** (single-partition RU cost) |
| `Contains`/`StartsWith` (Cassandra) | plain CQL has no LIKE | pushes down as `LIKE` **iff** the model declares a `[SearchIndex]` whose mode can serve the pattern |
| union of two entity shapes | most stores have no cross-collection UNION | MongoDB: one aggregation with `$unionWith`; relational: native `UNION`/`UNION ALL` — see [UNION](union.md) |

## Explain before you run

Every repository is an `IExplainableRepository<TEntity>`:

```csharp
QueryPlan plan = repo.Explain(options);
// plan.Statement          — the native query text (parameters as placeholders)
// plan.Residual           — what would run client-side (null when nothing)
// plan.ServerSideFiltering / plan.ClientEvaluation
// plan.PartitionScoped    — is the fetch pinned to a partition?
// plan.Notes              — one line per decision, gate and store-specific fact
```

`Explain` never executes; it reports what *would* happen, including which opt-ins would be needed.
Put it in a test to pin a query's shape, print it while developing, log it when diagnosing.

## Rules of thumb

- Filter on what the store indexes (keys, declared indexes) and everything pushes down by itself.
- When you get a refusal, first ask "should this be in the model?" (`[SearchIndex]`, a clustering
  key, a navigation) — the model is usually the right fix, the opt-in the explicit fallback.
- Treat `.AllowClientEvaluation()` / `.AllowFiltering()` in a diff as what they are: a reviewed,
  deliberate cost. That is the design working.
