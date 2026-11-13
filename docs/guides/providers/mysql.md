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
- `[SearchIndex]` materializes **no index** on MySQL (no trigram equivalent in the box) — `LIKE`
  still pushes down, unindexed, and [`Explain()`](../modeling/explain.md) says exactly that. The
  declaration stays portable; the plan differs per dialect, visibly.

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
