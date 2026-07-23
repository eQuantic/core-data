# The annotation vocabulary

Eleven annotations in `eQuantic.Core.Data.Modeling` declare the portable middle layer of the model.
They are deliberately named apart from `System.ComponentModel.DataAnnotations`, so both can coexist
without ambiguity. Each provider honours the subset that maps to its store **as the store's native
mechanism**; annotations outside a store's vocabulary are ignored, never errors.

## The matrix

| Annotation | Relational (PG / MySQL / MariaDB / SQL Server) | Cosmos DB | Cassandra | MongoDB |
|---|---|---|---|---|
| `[Entity("name")]` | table name | container name | table name | collection name |
| `[EntityKey]` (+`Generated`) | key column (identity readback when generated) | document `id` | point-lookup key column | the `_id` member |
| `[StoredAs("name")]` | column name | JSON element name (documents **and** queries) | column name | BSON element name |
| `[Unmapped]` | excluded | excluded (a query on it **refuses loudly**) | excluded | excluded |
| `[PartitionKey]` (+`Order`) | — ignored | partition key path; `Order` composes a **hierarchical (multi-hash) key**, up to 3 levels | partition key; `Order` composes a composite partition key | — ignored |
| `[ClusteringKey]` (+`Order`, `Descending`) | multi-column index with directions | composite index on the container's policy (2+ members) | real clustering key (rows physically ordered) | compound index with directions |
| `[ConcurrencyToken]` | versioned `WHERE` + bump; conflicts throw | `_etag` `If-Match` conditional replace | **lightweight transaction** (`IF version = old`, Paxos per write) | conditional replace/delete filtered on the read version |
| `[Counter]` | — ignored | — ignored | Cassandra `counter` column (increments only) | — ignored |
| `[SearchIndex]` (+`Mode`) | **GIN trigram** index on PostgreSQL (other dialects: no index, `LIKE` still pushes down) | — ignored (Cosmos indexes every path) | SASI index — `LIKE` pushes down natively | — ignored |
| `[TimeToLive(seconds)]` (class) | — ignored | container default TTL | table `default_time_to_live` | TTL index (per document, from the date member) |
| `[Facet]` (`Length`, `Precision`, `Scale`) | sized DDL: `varchar(n)` / `nvarchar(n)`, `numeric(p,s)` | — ignored | — ignored | — ignored |

“— ignored” always means *the store has no such concept*, which is the honest interpretation. When
a store **has** the concept but the declaration is incomplete or invalid there, the model build
throws with guidance instead.

## Reading the matrix as semantics, not features

The vocabulary is designed around **what you mean**, letting each store implement it its own way:

- `[ConcurrencyToken]` means *"a stale write must fail, never silently win"*. On SQL that is a
  versioned `UPDATE … WHERE version = @old`; on Cosmos an `If-Match`; on Cassandra a Paxos LWT; on
  MongoDB a version-filtered replace. All four throw `ConcurrencyConflictException` on the lost
  race — same exception, same meaning, four native mechanisms. The costs differ (an LWT is
  expensive — that is Cassandra's price for the semantics) and the
  [concurrency guide](../writing/concurrency.md) spells them out.
- `[ClusteringKey]` means *"I read this sorted, within the key"*. Cassandra materializes it
  physically; SQL and MongoDB build the ordered index; Cosmos declares the composite index. Query
  semantics never change — only the plan does.
- `[SearchIndex]` means *"substring/prefix matches on this member must be servable"*. On Cassandra
  it is the difference between `LIKE` working and not working (SASI serves it, and the `Mode` —
  `Contains` vs `Prefix` — bounds which patterns push down). On PostgreSQL `LIKE` already worked —
  the annotation adds the `pg_trgm` GIN index so it stops scanning.
- `[TimeToLive]` means *"this data expires"*. Note the honest nuance: Cosmos and Cassandra expire
  relative to the **write**; MongoDB expires per document relative to a **date member** (the
  lifecycle `CreatedAt` by convention, or an explicit member via fluent). The mechanism differences
  are documented, not hidden — see [Time to live](../writing/ttl.md).

## Compile-time diagnostics

The `eQuantic.Core.Data` package ships a Roslyn analyzer (no extra install), so the misuses that
are wrong on **every** provider surface while typing — as `EQD` warnings with the fix in the
message — instead of at model build:

| Id | Fires when |
|---|---|
| `EQD001` | `[ConcurrencyToken]` on a type no provider can version (not int/long/Guid/string) |
| `EQD002` / `EQD003` | several `[PartitionKey]` / `[ClusteringKey]` members share an `Order` (ambiguous composition) |
| `EQD004` | more than one `[EntityKey]` member (composite keys are fluent: `Key(x => new { … })`) |
| `EQD005` | `[TimeToLive]` with a non-positive lifetime |
| `EQD006` | an invalid `[Facet]` (negative values, `Scale` > `Precision`, `Length` on a non-string, `Precision` on a non-decimal, or an empty facet) |
| `EQD007` | an `[Unmapped]` member also carrying mapping annotations |
| `EQD008` | `[SearchIndex]` on a non-string member |
| `EQD009` | `[Counter]` on a non-integral member |
| `EQD010` | `[EntityKey(Generated = true)]` on a type identity cannot generate (non-integral) |
| `EQD011` | an empty storage name on `[Entity]` / `[StoredAs]` |

Provider-*relative* rules (what one store supports and another does not) deliberately stay at
runtime, where the provider is known and the message can be exact. Severity is Warning by default;
elevate per rule in `.editorconfig` (`dotnet_diagnostic.EQD001.severity = error`) if you want the
build to refuse.

## Details worth knowing

- **`[EntityKey]` and generated keys.** `[EntityKey(Generated = true)]` declares an identity key on
  relational stores: inserts omit the column and the flush assigns the database value back onto the
  entity. On MongoDB, `[EntityKey]` maps the member to `_id` — without it, a key member not
  literally named `Id` would persist as a plain field beside a driver-generated `ObjectId`.
- **`[StoredAs]` on Cosmos is end-to-end.** The provider's serializer extends the SDK's
  `CosmosLinqSerializer`, so the LINQ translation asks the *same contract* for member names —
  filters, sorts and projections on a renamed member hit the stored element. A rename can never
  desynchronize writes from queries.
- **`[Unmapped]` on Cosmos refuses in queries.** A member that does not exist in the stored
  document cannot be filtered on; the query throws `NotSupportedException` naming the member,
  instead of silently matching nothing.
- **Hierarchical partition keys.** Up to three `[PartitionKey(Order = …)]` members on a Cosmos
  entity compose a multi-hash key: the container is created with all paths, point writes build the
  multi-value key transparently, and partition inference declines (fans out) rather than guessing.
- **Composite keys are fluent-only.** An annotation declares one member; a composite key is a
  projection — `Key(x => new { x.OrderId, x.LineNo })` — and lives in the
  [fluent builder](fluent.md).
