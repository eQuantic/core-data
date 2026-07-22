# Apache Cassandra — deep dive

Cassandra is where the engine's philosophy earns its keep. The store's rules are strict — query by
the keys or pay for a scan; no OR; no `!=`; ordering only inside a partition — and this provider
turns each rule into either a **native mechanism** or an **explicit, visible decision**, never a
silent workaround.

## Model = access pattern

```csharp
model.Entity<Reading>(entity => entity
    .Table("readings")
    .PartitionKey(x => x.TenantId).PartitionKey(x => x.SensorId)   // composite partition
    .ClusteringKey(x => x.At, descending: true)                    // physical row order
    .Column(x => x.Value, "reading_value")                         // rename ([StoredAs] works too)
    .SearchIndex(x => x.Quality, CassandraSearchMode.Contains)     // SASI
    .ConcurrencyToken(x => x.Version)                              // writes become LWTs
    .TimeToLive(TimeSpan.FromDays(7)));                            // default_time_to_live
```

`EnsureCollection()` derives the full DDL: `PRIMARY KEY ((tenant_id, sensor_id), at)`,
`CLUSTERING ORDER BY (at DESC)`, `default_time_to_live`, and the SASI indexes.

## The query taxonomy

Every filter clause falls into one of these buckets — and `Explain()` tells you which, per query:

| Clause | Execution |
|---|---|
| equality / `IN` on partition key | native, partition-routed — the happy path |
| range on **clustering** key | native (`at > ?`) |
| range on **partition** key | native via **`token(col) op token(?)`** — the token-ring form |
| tuple comparison on clustering keys | native `(a, b) >= (?, ?)` |
| `Contains`/`StartsWith`/`EndsWith`/`Db.Like` on a `[SearchIndex]` column | native `LIKE` (SASI serves it; `Prefix` mode bounds the patterns honestly) |
| `CONTAINS` on collections | native, behind `.AllowFiltering()` |
| equality/range on other columns | native, behind **`.AllowFiltering()`** (a server-side scan — say so) |
| `OR` **across partition-key values** | **query splitting**: one native query per branch, parallel, merged + de-duplicated by primary key, per-branch `LIMIT` keeping top-N correct |
| anything CQL cannot express (`!=`, `NULL` compares, arbitrary code) | client-side **residual**, behind `.AllowClientEvaluation()` — over the pushed-down rows, `LIMIT` applied after |

The two opt-ins are the whole safety model: nothing scans and nothing runs in memory without a
visible call. When a residual is not even partition-scoped, the provider requires *both* opt-ins —
acknowledging a full-table fetch takes two signatures, deliberately.

## Writes

- Statements are **prepared once per session** and bound per write (the driver routes them
  token-aware); a commit executes them concurrently.
- `BeginTransactionAsync` defers writes into one atomic **`LOGGED BATCH`** — atomicity only, no
  isolation, exactly what Cassandra offers.
- `[ConcurrencyToken]` turns writes into **lightweight transactions** (`INSERT … IF NOT EXISTS` /
  `UPDATE … IF version = ?`) — Paxos per write, refused inside `LOGGED BATCH` (Cassandra's own
  restriction). `AddIfNotExistsAsync` exposes the bare LWT insert for token-less entities.
- **Counter tables**: `Counter(x => x.Hits)` models Cassandra counters honestly — increments only
  (`UpdateMany(filter, x => new Tally { Hits = x.Hits + n })`), never inserts, and the model
  validates the all-counters rule at build.
- Per-commit knobs: `Commit(o => o.WithConsistency(ConsistencyLevel.Quorum).WithTtl(…))`.

## Reads that respect the ring

- `ORDER BY` only on clustering keys — anything else throws with the reason (model the read order).
- No `OFFSET` exists: offset paging fetches `skip + take` and slices (fine shallow); deep paging
  uses the **driver's paging state** via `GetPageAsync`/`GetStreamAsync` — constant memory, opaque
  token.
- Typed `GroupByAsync` renders native CQL `GROUP BY` restricted to the **primary key prefix** (the
  store's rule); CQL has no `HAVING`, so predicate aggregates compute on the cluster as extra
  select cells and groups filter as they stream back — no extra rows travel either way.
- Aggregates (`Sum`/`Min`/`Max`/`Avg`) push down whenever the filter fully pushed; otherwise they
  aggregate client-side over the (gated) fetch.

## Consistency

Per query: `options.WithConsistency(ConsistencyLevel.LocalQuorum)`. Per commit: the save option
above. Defaults are the driver's — the provider adds no hidden policy.

## Registration recap

```csharp
services.AddCassandraSession(keyspace, contactPoints, port);
services.AddCassandraRepositories(model => { /* entities */ });
services.AddCassandraMigrations(assemblies);
```
