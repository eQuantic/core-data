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
- **Conditions are lightweight transactions.** A set-based write whose filter names members outside
  the primary key reads the rows the key finds and writes each `IF` those members still hold — a
  compare-and-set, counting the writes applied (the partition key has to be in the filter; a counter
  table takes no conditions). An increment of a plain column is written the same way, under
  `IF column = value read`. `UpsertAsync` is `INSERT … IF NOT EXISTS` then `UPDATE … IF`, on the
  **primary key only** — Cassandra holds nothing else unique, and an upsert on other members is
  refused. `InsertManyAsync` is `INSERT … IF NOT EXISTS` per row: its not-applied inserts are this
  provider's `DuplicateKeyException`s, since a plain insert overwrites by definition. A lightweight
  transaction that times out under contention is settled with a serial read; one that wrote a value
  computed from the row reaches you as a `TimeoutException`. See
  [Upserts and write outcomes](../writing/upserts.md).

## Enums as labels, JSON members, store-generated members

- **Enums as labels** — `[EnumType]` on the enum, or `model.Enum<TEnum>()`, gives the member a
  `text` column holding its `[StoredAs]` label (or its name). Writes, reads, filters (the
  compiler's `(int)x.State == 1` included) and set-based updates bind the label. Any other enum is
  stored as its number, in the CQL type of its underlying integral type.
- **JSON members** — `JsonDocument`, `JsonElement` and `JsonNode` are `text` columns holding their
  JSON; they come back equal, and `null` stays `null`.
- **Sequence** — Cassandra counters cannot be read atomically after an increment, so a sequence is a
  row per `<table>.<column>` in `equantic_sequences` (created on first use), advanced by a
  lightweight-transaction compare-and-set (`UPDATE … SET value = ? … IF value = ?`): only one writer
  can win a given value, so no number is handed out twice. One compare-and-set reserves every
  number a commit needs. It runs before a `LOGGED BATCH` (an LWT cannot join one): a failed batch
  leaves a gap, never a repeat.
- **Clock** — `toTimestamp(now())` in the `INSERT`, evaluated by the coordinator, then read back
  by the row's key at the commit's consistency. With several replicas, commit at a level whose reads
  see its writes (`QUORUM`); a read-back that does not see the insert fails loudly.
- **Updates never set them** — a `Modify` is an `INSERT` that leaves the store-generated columns
  out (Cassandra keeps the columns an insert does not name), a versioned update leaves them out of
  its `SET`, and a set-based update sets only what it names. `Add` and `AddIfNotExistsAsync`
  insert with the store's values.

```csharp
model.Entity<Receipt>(entity => entity
         .PartitionKey(x => x.Id)
         .Generated(x => x.Number)                          // Sequence
         .Generated(x => x.IssuedAt, StoreValue.Clock))     // Clock
     .Enum<Channel>();                                     // order does not matter
```

Primary-key, counter and concurrency columns cannot be store-generated. `Explain()` names the
labels, the JSON columns and the store-generated columns.

## Reads that respect the ring

- `ORDER BY` only on clustering keys — anything else throws with the reason (model the read order),
  unless the read opts into `.AllowClientEvaluation()`: then the fetched rows sort client-side,
  nulls placed as asked (`NullsOrder`), the limit after the sort. A clustering key is never null, so
  a placement on one is already honoured by CQL's order.
- No `OFFSET` exists: offset paging — `GetPagedAsync`, or `Skip`/`Take` on any read without a
  count — fetches `skip + take` and slices (fine shallow); deep paging uses the **driver's paging
  state** via `GetPageAsync`/`GetStreamAsync` — constant memory, opaque token.
- **Includes** load from the related table with one second `IN` query per path segment, under the
  read's own gates and opt-ins; partition the related table by the foreign key and cluster it in
  the order it is read, and a shaped include's limit runs on the cluster as `PER PARTITION LIMIT`.
  Navigations (members of an entity type, or collections of them) are not columns: the model leaves
  them out.
- **Relation counts** in a projection are `COUNT(*) … GROUP BY fk` when the related table is
  partitioned by the foreign key and the filter pushes down; otherwise the foreign keys are fetched
  and counted client-side, behind `.AllowClientEvaluation()` (a null test has no CQL form).
- **Distinct values** are a `GROUP BY` over a primary-key prefix that covers the partition key; any
  other selection deduplicates client-side, behind `.AllowClientEvaluation()`.
- Typed `GroupByAsync` renders native CQL `GROUP BY` restricted to the **primary key prefix** (the
  store's rule); CQL has no `HAVING`, so predicate aggregates compute on the cluster as extra
  select cells and groups filter as they stream back — no extra rows travel either way.
- Aggregates (`Sum`/`Min`/`Max`/`Avg`) push down whenever the filter fully pushed; otherwise they
  aggregate client-side over the (gated) fetch.
- **Filters across relations** resolve the related keys first — a read of the related table through
  the same gates, **under the read's own opt-ins** (`SELECT DISTINCT` when the key is its partition
  key) — then `IN` over them where the key serves it; a negated test (`NOT IN`) runs client-side behind
  `.AllowClientEvaluation()`. A set-based write resolves its keys with no opt-ins, so its related read
  has to be native, and its keys must address the written table's partition; an update restricted to
  the partition alone updates each row it finds by its full key. See
  [Relations and text in filters](../querying/predicates.md).
- **Text ignoring case** pushes down as `LIKE` on a SASI index declared case-insensitive
  (`SearchIndex(x => x.Name, CassandraSearchMode.Contains, caseInsensitive: true)` or
  `[SearchIndex(CaseInsensitive = true)]`, created with a non-tokenizing, `case_sensitive: false`
  analyzer); elsewhere it is the client-side residual. An `IN` on a column outside the primary key,
  which CQL refuses, also runs client-side.

## Consistency

Per query: `options.WithConsistency(ConsistencyLevel.LocalQuorum)`. Per commit: the save option
above. Defaults are the driver's — the provider adds no hidden policy.

## Registration recap

```csharp
services.AddCassandraSession(keyspace, contactPoints, port);
services.AddCassandraRepositories(model => { /* entities */ });
services.AddCassandraMigrations(assemblies);
```
