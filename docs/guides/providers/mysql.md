# MySQL / MariaDB — deep dive

One package, two dialects — because the engines genuinely differ and the dialect is where the
truth lives.

## MySQL vs MariaDB

| Concern | MySQL dialect | MariaDB dialect |
|---|---|---|
| Registration | `AddMySqlDatabase(…)` | `AddMariaDbDatabase(…)` |
| Generated keys | `LAST_INSERT_ID`-based readback | **`INSERT … RETURNING`** (native) |
| Filtered (partial) indexes | refused with guidance — the engine has none | refused with guidance |
| Everything else | identical — both run the shared relational engine over MySqlConnector | identical |

## Capabilities

- Atomic batched commit in a transaction, identity readback.
- Named locks through `GET_LOCK`, a **session** lock no transaction releases: inside a transaction the
  engine releases it right after the commit or rollback, outside one on dispose. Lock names are
  **server-wide**, and MySQL rounds its wait to whole seconds (MariaDB does not) — see
  [Named locks](../writing/locks.md).
- Concurrency tokens, composite keys, facets (`varchar(n)`, `decimal(p,s)`), converters,
  navigations/includes, GROUP BY/HAVING, UNION/UNION ALL — the full
  [relational engine surface](../concepts/contracts.md).
- Read shapes on both dialects: shaped includes ranked with `ROW_NUMBER()` (MySQL 8.0, MariaDB
  10.2 or later), relation counts as correlated subqueries, nulls placed by ordering on
  `CASE WHEN col IS NULL` (neither engine has `NULLS FIRST`), `LIMIT`/`OFFSET` pages without a count
  and `SELECT DISTINCT` — see [Querying](../querying/includes.md).
- Filters across relations run as a correlated `EXISTS` / `NOT EXISTS` on both dialects, in reads,
  counts and set-based writes. A set-based write whose relation reads the very table it writes (a
  self-referencing navigation), which both engines refuse in one statement (error 1093), reads the
  matching keys first with `SELECT … FOR UPDATE` inside its transaction and writes by them, the rest
  of the filter still applied — see [Relations and text in filters](../querying/predicates.md). Text ignoring case is
  `LOWER(col) = LOWER(@p)` / `LOWER(col) LIKE LOWER(@p) ESCAPE '!'`; the default collations also
  ignore accents.
- `[SearchIndex]` materializes **no index** on MySQL (no trigram equivalent in the box) — `LIKE`
  still pushes down, unindexed, and [`Explain()`](../modeling/explain.md) says exactly that. The
  declaration stays portable; the plan differs per dialect, visibly.

## Upserts and write outcomes

`ON DUPLICATE KEY UPDATE` fires on **any** unique key and cannot be told which one is the target, so
`UpsertAsync` reads the target in a transaction — without a lock first, so that two upserts of a row
that is not there hold no gap lock to deadlock on — and updates the row it finds under
`SELECT … FOR UPDATE`, or inserts it; when a concurrent insert of the target wins (error 1062, which
rolls back that statement alone) it updates the row that won. Outside an explicit transaction it runs
again when InnoDB picks it as a deadlock victim (1213); inside one, the store rolled the caller's
transaction back and the error reaches the caller. An upsert on a key the store generates is refused
on MySQL, which cannot hand that key back, and runs on MariaDB, with `RETURNING`. `InsertManyAsync(…, skipDuplicates: true)` inserts row by row in one
transaction, discarding only error 1062; `UpdateAndGetAsync` locks the matching rows, updates them and
reads them back in one transaction (neither engine has `RETURNING` for an `UPDATE`). The data source
always reports the rows a statement **found** (`UseAffectedRows=false`, whatever the connection string
says), so `UpdateManyAsync` counts the rows it matched. Error 1062 — and a bulk load's duplicate-key
warning, which `LOAD DATA LOCAL` would otherwise skip silently — is `DuplicateKeyException`, and the
load is undone whole: its own transaction rolled back, or, inside the caller's, a savepoint the engine
set before it. See
[Upserts and write outcomes](../writing/upserts.md).

## Honest refusals you may meet

> `MySqlDialect has no filtered-index structure; use a default index, or the store's native tooling
> via Run(...).`

The fluent surface never pretends: what the engine cannot do, the dialect names, and the `Run(...)`
escape hatch in [migrations](../migrations/index.md) remains for engine-specific DDL.

## Registration recap

```csharp
services.AddMySqlDatabase(connectionString, model => { /* entities */ });    // or AddMariaDbDatabase
services.AddMySqlRepositories();
services.AddMySqlMigrations(assemblies);
```
