# PostgreSQL — deep dive

The reference relational provider: PostgreSQL is the dialect with the richest capability set, and
the shared relational engine exercises all of it.

## Capabilities at a glance

| Area | Support |
|---|---|
| Atomic commit | one batched flush in a transaction, `RETURNING` for identity keys |
| jsonb | dictionary/document members map to `jsonb` columns; GIN-indexable |
| Search | `[SearchIndex]` → **GIN trigram** (`pg_trgm`): `Contains`/`EndsWith` stop scanning |
| Indexes | filtered (partial) with typed predicates, GIN, composite with directions, unique |
| Concurrency | versioned token (`int`/`long`/`Guid`) — see [Concurrency](../writing/concurrency.md) |
| Composite keys | `Key(x => new { … })`, tuple point-lookups, composite `PRIMARY KEY` DDL |
| Facets | `varchar(n)`, `numeric(p,s)` |
| Includes | reference/collection with follow-up `IN` queries, dotted nested paths |
| GROUP BY / HAVING / UNION | fully native |
| Resilience | opt-in transient retry — see [Resilience](../operations/resilience.md) |

## jsonb members

A `Dictionary<string, string>` (or similar document-shaped) member maps to `jsonb`:

```csharp
public Dictionary<string, string> Attributes { get; set; } = new();
```

```csharp
// containment and key filters push down as jsonb operators:
var tagged = await repo.GetFilteredAsync(o => o.Attributes["color"] == "red");

// and the migration can GIN-index the column:
.Index(x => x.Attributes, o => o.Gin())
```

## Trigram search

`[SearchIndex]` on a text member makes `EnsureCollection()` run
`CREATE EXTENSION IF NOT EXISTS pg_trgm` and build
`CREATE INDEX … USING GIN (column gin_trgm_ops)`. Semantics do not change — `LIKE` always pushed
down on SQL — the index makes leading-wildcard matches (`%term%`) indexable instead of sequential.
Requires the privilege to create extensions (typical in owned databases; ask your DBA otherwise —
the extension ships with stock PostgreSQL).

## Things the dialect is honest about

- `GIN` indexes cannot be unique (`Unique().Gin()` throws with the reason).
- Strings without a `[Facet(Length = …)]` map to `text` — idiomatic PostgreSQL; size only when the
  domain demands it.

## Registration recap

```csharp
services.AddPostgreSqlDatabase(connectionString, model => { /* entities */ });
services.AddPostgreSqlRepositories();
services.AddPostgreSqlMigrations(assemblies);
services.AddRelationalResilience(o => o.RetryCommits = true);   // optional
```
