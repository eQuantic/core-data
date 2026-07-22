# Explain — the model never lies

Every model has an `Explain()` that reports **every mapping decision** — what convention decided,
what annotations and fluent configuration declared, and what the store will actually be told. It is
the same contract queries have: read the report instead of guessing.

## Relational

```csharp
var report = model.Explain(dialect);
```

```text
SaleOrder -> table "sale_orders"
  key: (OrderId "order_id", LineNo "line_no") composite, client-generated
  concurrency token: Version "version"
  column: Name "client_name" (varchar(200))
  column: Email "email" (text) (converted from EmailAddress)
  clustering: placed_at DESC (multi-column index)
  search index: Description "description" (GIN trigram via pg_trgm; LIKE stops scanning)
  lifecycle: soft delete + live-rows filter (IEntityTimeEnded)
```

Every line answers a question you would otherwise answer by reading DDL: what did the column end up
being called, what type did the facet produce, is the token active, did the dialect materialize the
search index or ignore it (`no index structure on this dialect; LIKE still pushes down, unindexed`).

## Cosmos DB

```csharp
var report = cosmosModel.Explain();
```

```text
Order -> container "orders"
  partition key: (/tenant, /region) hierarchical (multi-hash)
  clustering: /kind ASC, /at DESC (composite index on the container's policy)
  id: Code ([EntityKey])
  default TTL: 2592000s (container-level; documents expire unless they override it)
  concurrency token: _etag (writes replace conditionally with If-Match)
```

## Cassandra

```csharp
var report = cassandraModel.Explain();
```

```text
Reading -> table "readings"
  partition key: (tenant_id, sensor_id)
  clustering keys: at DESC
  key column: "sensor_id" (point lookups)
  concurrency token: "version" (writes are LWTs: IF version = old, Paxos per write)
  default TTL: 604800s (table default_time_to_live; writes can override)
  column: Value "reading_value" (double)
  search index: "quality" (SASI Contains; LIKE pushes down)
```

## MongoDB

```csharp
var report = mongoModel.Explain();   // reads the driver's actual class maps — not a parallel copy
```

```text
Product -> collection "products"
  id: Code "_id"
  member: Label "l"
  converts: Grade (stored as String)
  clustering: Category ASC, Score DESC (compound index)
  TTL: documents expire 3600s after CreatedAt (per-document TTL index)
```

## Why this matters

Mapping bugs are the silent kind: a renamed column that queries miss, a token that never activated,
an index you believe exists. `Explain()` turns each into one line you can read in a test:

```csharp
Assert.That(model.Explain(dialect), Does.Contain("concurrency token: Version"));
```

Pin the lines you rely on. When a refactor changes the mapping, the test fails before production
does. The query-side counterpart — what a *read* will do — is
[`IExplainableRepository.Explain(options)`](../querying/pushdown.md#explain-before-you-run).
