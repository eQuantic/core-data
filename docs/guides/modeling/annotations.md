# The annotation vocabulary

Thirteen annotations in `eQuantic.Core.Data.Modeling` declare the portable middle layer of the model.
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
| `[SearchIndex]` (+`Mode`, `CaseInsensitive`) | **GIN trigram** index on PostgreSQL (other dialects: no index, `LIKE` still pushes down) | — ignored (Cosmos indexes every path) | SASI index — `LIKE` pushes down natively, and case-insensitive matching too when `CaseInsensitive` | — ignored |
| `[TimeToLive(seconds)]` (class) | — ignored | container default TTL | table `default_time_to_live` | TTL index (per document, from the date member) |
| `[Facet]` (`Length`, `Precision`, `Scale`) | sized DDL: `varchar(n)` / `nvarchar(n)`, `numeric(p,s)` | — ignored | — ignored | — ignored |
| `[EnumType]` (enum; `[StoredAs]` on its members) | stored as its **labels**: a PostgreSQL enum type, a MySQL/MariaDB `ENUM(…)`, SQL Server text held to the labels by a `CHECK` | the label as a JSON string (documents **and** queries) | the label as `text` | the label as a BSON string |
| `[StoreGenerated]` (+`StoreValue`) | the store assigns it on insert: an identity or a sequence object (`Sequence`), a clock default (`Clock`); read back by `RETURNING` / `OUTPUT INSERTED` / the row by its key (MySQL); never written by an update | an atomic `Increment` patch on a counter item; `GetCurrentDateTime()` read once per commit | a sequence advanced by lightweight transactions; `toTimestamp(now())` | an atomic `$inc` on a counter document, majority-acknowledged and outside the open transaction (a rollback leaves a gap); the server's `hello` clock read once per commit |

“— ignored” always means *the store has no such concept*, which is the honest interpretation. When
a store **has** the concept but the declaration is incomplete or invalid there, the model build
throws with guidance instead.

## Types the vocabulary maps without an annotation

A member of type `JsonDocument`, `JsonElement` or `JsonNode` (and its subtypes) holds **any JSON** (an
object, an array or a scalar) and every engine stores it in its own JSON form: `jsonb` on PostgreSQL,
`JSON` on MySQL and MariaDB, text held to being JSON by a `CHECK` on SQL Server, an embedded value on
MongoDB and Cosmos DB, and text holding the JSON on Cassandra. It comes back equal, and a null stays null.
No engine leaves such a member out of its mapping.

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
| `EQD011` | an empty or whitespace-only storage name on `[Entity]` / `[StoredAs]` / `[EnumType]` |
| `EQD012` | `[StoreGenerated]` on a member no store generates in that kind (a sequence numbers an `int`, `long` or `short`; the clock stamps a `DateTime` or `DateTimeOffset`) |
| `EQD013` | `[StoreGenerated]` on the key (declare `[EntityKey(Generated = true)]`) or on the concurrency token |
| `EQD014` | an `[EnumType]` enum with an empty label, or a label naming two members |

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
- **`[EnumType]` means "the store holds the labels and nothing else".** Each member's label is its
  `[StoredAs]` name, or its own name, and each member needs a value of its own: two members of one value
  (an alias) are refused, since C# cannot tell them apart and `Enum.GetName` does not say which name
  such a value takes. Filters and set-based updates compare and write labels, including
  the `(int)x.State == 1` shape the compiler produces. The enum still **compares and orders by its
  members' values**, as C# does, never by the labels' text nor by the order PostgreSQL or MySQL declared
  them in: `x.Priority >= Priority.Medium` (on a nullable member too, and under a negation, where a null
  passes as it does in C#) is membership in the labels whose member satisfies it, and
  `OrderBy(x => x.Priority)` orders by the value each label stands for — a `CASE` on the relational
  engines, a `$switch` on MongoDB, one query per member on Cosmos DB, and client-side behind
  `.AllowClientEvaluation()` on Cassandra (see each provider). `MinAsync`/`MaxAsync`, a group's key and
  `HAVING`, and a union's order follow the same values; a sum or an average of labels is refused (see
  [Aggregates](../querying/aggregates.md)). An enum without it keeps being stored as its number. On the relational engines the store itself refuses another value; on the document and
  wide-column engines the label is a plain string, so the guarantee is the provider's. Switching an
  existing enum from numbers to labels is a data migration, and `eqdata migrations add` says what to
  write instead of generating it (see [Migrations](../migrations/index.md)).
- **`[StoreGenerated]` means "the store, not the application, gives this value".** The insert leaves it
  out and reads it back onto the entity; no update, staged or set-based, ever writes it; a bulk insert
  leaves it to the store. `Sequence` hands out each number once and they grow; `Clock` is the store's own
  time at the insert, in UTC, never the client's. The relational builders also take a SQL default
  (`Generated(x => x.Member, "now()")`) for a schema another tool made.
- **Composite keys are fluent-only.** An annotation declares one member; a composite key is a
  projection — `Key(x => new { x.OrderId, x.LineNo })` — and lives in the
  [fluent builder](fluent.md).
