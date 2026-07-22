# SQL Server — deep dive

The shared relational engine over `Microsoft.Data.SqlClient`, with SQL Server's genuine strengths
(filtered indexes, `OUTPUT INSERTED`) and its dialect quirks handled where they belong.

## Capabilities

| Area | Support |
|---|---|
| Atomic commit | batched flush in a transaction; identity keys via `OUTPUT INSERTED` |
| Filtered indexes | fully supported, typed predicates (`o.Filtered(x => x.Quantity > 0)`) |
| Concurrency | versioned token — see [Concurrency](../writing/concurrency.md) |
| Composite keys / facets / converters / includes | full relational engine surface |
| GROUP BY / HAVING / UNION | fully native |
| Paging | `OFFSET … FETCH`; keyset continuation over the key |

## Dialect specifics

- **Strings default to `nvarchar(450)`** — deliberately within the index key-size limit, so
  conventional string columns are always indexable. Size explicitly with `[Facet(Length = …)]` /
  `Facet(x => …, length: …)`, which produces `nvarchar(n)`.
- Decimals with `[Facet(Precision, Scale)]` produce `numeric(p,s)`.
- `[SearchIndex]` materializes no index on this dialect (SQL Server's full-text search is a
  different subsystem with different semantics — `CONTAINS` word-matching, not `LIKE` substring
  matching; mapping one onto the other would change meaning). `LIKE` pushes down as always, and
  `Explain()` reports the declaration as unindexed here.

## Registration recap

```csharp
services.AddSqlServerDatabase(connectionString, model => { /* entities */ });
services.AddSqlServerRepositories();
services.AddSqlServerMigrations(assemblies);
services.AddRelationalResilience();     // optional transient-fault retry
```
