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
  token-aware); a commit executes its plain writes concurrently, and its lightweight transactions
  (a concurrency token's writes) one at a time per partition.
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
  provider's `DuplicateKeyException`s, since a plain insert overwrites by definition. Neither a
  set-based update nor an upsert's update assigns the primary key or a store-generated member,
  whichever path it takes (`NotSupportedException`). See [Upserts and write outcomes](../writing/upserts.md).
- **One partition's transactions run one at a time.** Paxos runs per partition, so transactions
  proposed at once on one partition contend with each other until they time out. A write that runs a
  transaction per row (a conditional or computed set-based write, `InsertManyAsync`, the token writes
  of a commit) runs a partition's one after the other, and up to 16 partitions side by side.
- **A timeout is settled only by what a serial read proves.** A lightweight transaction that times
  out has an outcome nobody knows; a read at the serial consistency completes or abandons for good the
  round it left. A write of values set outright counts as applied when the row holds them, and is
  written again otherwise; an insert counts as applied when the row is the entity's. A value computed
  from the row (`x.Uses + 1`) can come back to a value it held, so no read says whether it applied:
  that timeout reaches you as a `TimeoutException` with the driver's inside — unless the write
  conditions on a concurrency token and bumps it (an upsert's update): versions only grow, so a row
  still at the version read was not written, and the write runs again.

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
  number a commit — or an `InsertManyAsync` — needs. It runs before a `LOGGED BATCH` (an LWT cannot
  join one): a failed batch, or an insert that does not apply, leaves a gap, never a repeat. A
  compare-and-set that times out advances again from the value a serial read finds: if the first one
  applied, its numbers are skipped.
- **Clock** — `toTimestamp(now())` in the `INSERT`, evaluated by the coordinator, then read back
  by the row's key — only the clock: a sequence's number is on the entity before the insert. After an
  `INSERT … IF NOT EXISTS` (`AddIfNotExistsAsync`, `InsertManyAsync`, an upsert, a versioned `Add`)
  the read runs at the serial consistency the transaction ran at; after a plain insert, at the
  commit's consistency. With several replicas, commit at a level whose reads see its writes
  (`QUORUM`); a read-back that does not see the insert fails loudly.
- **Updates never set them** — a `Modify` is an `INSERT` that leaves the store-generated columns
  out (Cassandra keeps the columns an insert does not name), a versioned update leaves them out of
  its `SET`, and a set-based update or an upsert's update that names one is refused. `Add` and
  `AddIfNotExistsAsync` insert with the store's values.

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
- **Includes** load from the related table with a second `IN` query per path segment (a query per
  chunk of 100 parents' keys), under the read's own gates and opt-ins: a related read that names none
  of the related table's partitions (its foreign key is not the partition key) is a scan, behind
  `.AllowFiltering()`, and a foreign key outside the primary key takes no CQL `IN`, so the table is
  read whole and its rows kept client-side, behind both opt-ins. Partition the related table by the
  foreign key and cluster it in the order it is read, and a shaped include's limit runs on the
  cluster as `PER PARTITION LIMIT`.
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
  the same gates, **under the read's own opt-ins** — then `IN` over them where the key serves it; a
  negated test (`NOT IN`) runs client-side behind `.AllowClientEvaluation()`. A related read that names
  none of the related table's partitions is a scan, behind `.AllowFiltering()`: a test with no
  condition (`x.Runs.Any()`, `!x.Runs.Any()`, `x.Box == null`) reads every related row's key
  (`SELECT DISTINCT` when the key is the partition key). A set-based write carries no opt-ins, so its
  related read has to be **partition-scoped**: by the relation's condition (`x.Repo.WorkspaceId == w`
  on repositories partitioned by workspace), or — when the write's filter names the written table's
  partition — by the rows the write finds, whose keys are read from the related table by its partition
  key (`x.Space == s && !x.Runs.Any()` reads the runs of the items of `s`, in chunks). Any other is
  refused, before anything is read. A negated test in a write leaves out, of the rows the rest of the
  filter finds, those whose key the related read returned, and writes each other row by its full key
  in a lightweight transaction holding the columns the test read; a row the write meets that was not
  among the rows its relation was read for is left alone. See
  [Relations and text in filters](../querying/predicates.md).
- **Long `IN` lists** — a resolved relation's keys, an include's parents, a long `ids.Contains(x.Id)`
  on the primary key — run in chunks of 100 keys wherever rows are read or written: a query per chunk,
  a few at a time, merged and de-duplicated by primary key (as an OR split is), and a set-based write's
  keyed `DELETE`, `UPDATE`, count and row read per chunk. A count, an aggregate, a group or a page has
  one answer per statement, so it binds the whole list in one — refused past the 65,535 values one
  statement can bind.
- **Plain writes and lightweight transactions on the same rows** are ordered by their timestamps: a
  plain write (a commit, a keyed set-based update or delete) carries the client's clock — the driver's
  default — and a lightweight transaction (a conditional or row-by-row set-based write, an upsert) the
  coordinator's. Keep the clocks synchronized, or give the session server-side timestamps (a
  `WithTimestampGenerator` whose `Next()` answers `long.MinValue`), or a later write can lose to an
  earlier one.
- **Text ignoring case** pushes down as `LIKE` on a SASI index declared case-insensitive
  (`SearchIndex(x => x.Name, CassandraSearchMode.Contains, caseInsensitive: true)` or
  `[SearchIndex(CaseInsensitive = true)]`, created with a non-tokenizing, `case_sensitive: false`
  analyzer); elsewhere it is the client-side residual. An `IN` on a column outside the primary key,
  which CQL refuses, also runs client-side.

## Consistency

Per query: `options.WithConsistency(ConsistencyLevel.LocalQuorum)`. Per commit: the save option
above. Defaults are the driver's — the provider adds no hidden policy.

**One serial consistency.** A lightweight transaction's Paxos rounds run at a serial consistency, and
the read that settles what it decided has to run at the same one. The provider takes the session's —
`SERIAL`, the driver's default — pins every transaction to it (sequences, leases, inserts, upserts,
conditional and computed set-based writes, a commit's token writes) and reads at it every time it
reads at a serial level (a conditional write's rows, a timeout's settling read, a transaction's
read-back). For `LOCAL_SERIAL`, build the cluster with it and register that session in place of
`AddCassandraSession` (the keyspace has to exist):

```csharp
services.AddSingleton<ISession>(_ => Cluster.Builder()
    .AddContactPoints(contactPoints)
    .WithQueryOptions(new QueryOptions().SetSerialConsistencyLevel(ConsistencyLevel.LocalSerial))
    .Build()
    .Connect(keyspace));
```

`LOCAL_SERIAL` runs Paxos among the local data center's replicas only: two data centers can each win
a compare-and-set of the same row, so a sequence could hand a number out twice and a named lock be
held in two places at once. Choose it only when every writer of a row reaches the keyspace through one
data center (or the cluster has one).

## Named locks

A named lock is a **lease**: a row in the `equantic_locks` table (created on first use), taken with
`INSERT … IF NOT EXISTS USING TTL`, renewed with `UPDATE … USING TTL … IF owner = ?` and released with
`DELETE … IF owner = ?` — lightweight transactions all, pinned to the session's serial consistency, so
of two holders exactly one is applied. It
is written outside the `LOGGED BATCH`, and a dead holder's row expires with its TTL, in whole
seconds — see [Named locks](../writing/locks.md).

## Registration recap

```csharp
services.AddCassandraSession(keyspace, contactPoints, port);
services.AddCassandraRepositories(model => { /* entities */ });
services.AddCassandraMigrations(assemblies);
```
