# How the engine works

A tour of the moving parts — for contributors, extenders, and anyone who wants to trust the
machine by understanding it.

## The layers

```
┌─────────────────────────────────────────────────────────────┐
│ Contracts (eQuantic.Core.Data)                              │
│   IRepository / IUnitOfWork / QueryOptions / Migrations     │
│   Modeling annotations · DataConventions · EntityLifecycle  │
├─────────────────────────────────────────────────────────────┤
│ Interpreters (store-agnostic, eQuantic.Core.Data.Query)     │
│   FilterInterpreter  → QueryFilter IR                       │
│   UpdateInterpreter  → UpdateAssignment IR                  │
│   GroupInterpreter   → GroupQuery IR (+ HAVING)             │
│   UnionInterpreter   → UnionQuery                           │
├──────────────┬──────────────┬───────────────┬───────────────┤
│ Relational   │ MongoDB      │ Cosmos DB     │ Cassandra     │
│ engine +     │ (driver      │ (SDK LINQ +   │ (CQL renderer │
│ SqlDialects  │ LINQ + class │ CosmosLinq-   │ + pushdown    │
│ (PG/My/MS)   │ maps)        │ Serializer)   │ planner)      │
└──────────────┴──────────────┴───────────────┴───────────────┘
```

## The interpreters — one IR, many renderers

Every lambda (filters, update factories, group shapes) is interpreted **once, store-agnostically**
into a small IR: comparisons, `IN`, ranges, string operations, logical nodes; set/increment/
collection assignments; group keys and aggregate bindings. Captured variables are partially
evaluated to constants at this stage (built on eQuantic.Linq.Expressions — which also makes
filters wire-serializable).

Providers then *render* the IR: SQL through a `SqlDialect`, CQL through the Cassandra renderer,
Mongo filters/updates through the driver, Cosmos through the SDK's LINQ. What a renderer cannot
express **falls out as the residual** — the raw material of the
[gates](../querying/pushdown.md). Because validation happens in the shared interpreter, rejection
messages and accepted shapes are identical across providers; because rendering is per-store, each
provider does its native best.

## The relational engine

One engine (`eQuantic.Core.Data.Relational`), four thin dialects. The dialect owns exactly what
differs between engines: identifier quoting, naming conventions, paging syntax, DDL type names
(facets included), generated-key retrieval (`RETURNING` vs `OUTPUT INSERTED` vs last-insert-id),
index structures (GIN, filtered, trigram), and the rendering of constructs engines disagree on.
Everything else — the batch flush, concurrency tokens, includes, unions, group-by, materialization
— is written once. A dialect is ~200 lines; that is the [extension point](extending.md).

## The write path

`Add/Modify/Remove` → `EntityLifecycle` stamps (time, WHO, soft-delete conversion, token bumps) →
the provider stages the native write (SQL batch command, Mongo `WriteModel`, Cassandra bound
statement, Cosmos point operation) → `Commit` flushes the store's best batch → results are checked
(affected rows, `[applied]`, matched counts) and `ConcurrencyConflictException` surfaces lost
races. No tracker, no snapshots: the staged write *is* the state.

## Generated accessors — reflection out of the hot paths

The package ships a **source generator** (beside the analyzer, no extra install) that emits a
reflection-free accessor per entity — construction, member reads and member writes as direct code
behind a name switch — registered into the engine's `EntityAccessors` registry by a module
initializer. Relational materialization and column reads consult the registry first; **reflection
remains the fallback contract**, so assemblies compiled without the generator behave identically.

Honesty notes: entities the generated code could not honor faithfully (init-only or non-public
setters on mapped-shaped members, no accessible parameterless constructor) are skipped entirely —
no accessor beats a lossy one — and stay on the reflection path. Measured effect on the
[benchmarks](../operations/benchmarks.md): within noise (the database dominates); the point of the
accessors is eliminating the reflection dependence itself — the groundwork for trimming/NativeAOT
support.

## The model registry

Each provider builds an immutable model at startup (annotations pre-pass seeds each entity
builder; fluent calls override; `Build()` validates and freezes). Configurations carry both sides
of every mapping (member ↔ stored name), so renames flow through queries, writes, DDL, projections
and migrations from one source of truth — and `Explain()` prints it.

## Protection of the engine seam

The interpreter/IR namespace (`eQuantic.Core.Data.Query`) is public — providers in other packages
need it — but marked `[EditorBrowsable(Never)]`: it is SPI, not API. The support policy is
explicit: application code targets the repository contracts; the IR may evolve with provider needs.
