# eQuantic.Core.Data

**A provider-agnostic Repository + Unit of Work for .NET, where filtering, sorting and paging are
authored typed and fluent — not as stringly-typed configuration.**

```csharp
var repo = unitOfWork.GetAsyncRepository<OrderData, Guid>();

var page = await repo.GetPagedAsync(
    PageRequest.Of(pageIndex: 1, pageSize: 20),
    new QueryOptions<OrderData>()
        .Where(o => o.Total, FilterOperator.GreaterThan, 100m)
        .And(o => o.Customer.Name, FilterOperator.Contains, term)
        .OrderByDescending(o => o.CreatedAt)
        .Include(nameof(OrderData.Customer))
        .NoTracking());

// page is a PagedResult<OrderData>: Items + TotalCount + PageIndex/PageSize/PageCount + Has*Page
```

## Why

The Repository pattern keeps your domain ignorant of the persistence engine — you code against
`IRepository<TEntity, TKey>`, and the Entity Framework (or other) provider supplies the
implementation. What usually rots is the *query surface*: dozens of `GetPaged`/`GetFiltered`
overloads and `Action<Configuration>` callbacks, with filters and sorts passed as magic strings.

`eQuantic.Core.Data` v5 collapses that: **one method per operation**, each taking a single
`QueryOptions<TEntity>` that you compose fluently and typed, backed by the
[eQuantic.Linq](https://github.com/eQuantic/core-linq) query engine.

## Getting started

This package is the **contracts** — pair it with a provider that supplies the engine:

```bash
dotnet add package eQuantic.Core.Data
dotnet add package eQuantic.Core.Data.EntityFramework.SqlServer   # or PostgreSql / MySql / MongoDb / CosmosDb
```

Give your entities a key via `IEntity<TKey>` (the key is exposed through `GetKey`/`SetKey` — no mandated
`Id` property):

```csharp
using eQuantic.Core.Data.Repository;

public class OrderData : IEntity<Guid>
{
    public Guid Id { get; set; }
    public decimal Total { get; set; }
    public Guid GetKey() => Id;
    public void SetKey(Guid key) => Id = key;
}
```

Register the provider's unit of work and repositories — the wiring is provider-specific (see the
[Entity Framework Core provider](https://github.com/eQuantic/core-data-entityframework)) — then inject
`IUnitOfWork`, ask it for a repository, and query with a `QueryOptions` (the snippet at the top).

## How you query

`QueryOptions<TEntity>` mirrors the eQuantic.Linq query builders, so filters read like code and
fail at compile time — not at runtime:

```csharp
new QueryOptions<OrderData>()
    .Where(o => o.Total, FilterOperator.GreaterThanOrEqual, 100m)   // typed member selector
    .And(o => o.Status, FilterOperator.Equal, OrderStatus.Paid)     // clauses fold left to right:
    .Or(o => o.Customer.IsVip, FilterOperator.Equal, true)          //   (total>=100 AND paid) OR vip
    .OrderByDescending(o => o.CreatedAt)
    .ThenBy("customer.name");                                        // string path for dynamic columns
```

You reach for whichever filter form fits — all end up as one predicate the provider translates:

| Form | When |
|------|------|
| `Where(selector, op, value)` / `And` / `Or` | **Primary** — typed, fluent, compile-checked. |
| `Where(string path, op, value)` | Dynamic column names; operator and value stay typed. |
| `Where(ISpecification<T>)` | A reusable domain rule ([specification pattern](Repository.md)). |
| `Where(Expression<Func<T, bool>>)` | An arbitrary predicate you already hold. |
| `Where(ExpressionModel<T>)` | A serialized filter — built in code or received over the wire. |
| `Where("total:gt(100)")` | The boundary where a filter arrives **as a query string** (e.g. a `filterBy` parameter). Prefer the typed form in code. |

The query-string grammar behind the string forms (`total:gt(100),status:eq(Paid)`) is documented in
the [eQuantic.Linq query-string reference](https://github.com/eQuantic/core-linq/blob/main/docs/query-string-syntax.md).

## Paging that tells you what you got

```csharp
PagedResult<OrderData> page = await repo.GetPagedAsync(PageRequest.Of(2, 20), options);
// page.Items, page.TotalCount, page.PageIndex, page.PageSize, page.PageCount,
// page.HasPreviousPage, page.HasNextPage
```

`PageRequest` is one-based (`Skip`/`Take` derived); `PagedResult<T>` carries the items *and* the
totals — no second count call, no bare `IEnumerable<T>`.

## Provider-agnostic by design

This package is the **contracts** (`IRepository`, `IUnitOfWork`, `QueryOptions`, `PageRequest`,
`PagedResult`, specifications). The persistence engine is kept out of the type signatures —
`IRepository<TEntity, TKey>`, not `IRepository<TUnitOfWork, TEntity, TKey>`. The Entity Framework
implementation lives in the provider packages (`eQuantic.Core.Data.EntityFramework` and friends).

## Native providers — MongoDB, Azure Cosmos DB, Apache Cassandra, PostgreSQL, MySQL, SQL Server

The same contracts implemented **directly on each official driver — no Entity Framework** — with a
lean write model: `Add`/`Modify`/`Remove` buffer typed writes and one native batch runs on `Commit`
(no change tracking or snapshotting). Explicit transactions are opt-in: multi-document sessions on
MongoDB (reads inside the transaction see its writes), a single-partition `TransactionalBatch` on
Cosmos, an atomic `LOGGED BATCH` on Cassandra — and on PostgreSQL the commit itself is **atomic**
(one batched flush in a transaction, generated keys read back — `RETURNING` on PostgreSQL,
`OUTPUT INSERTED` on SQL Server — and explicit transactions spanning commits with
read-your-writes). The relational providers are thin dialects over the shared
`eQuantic.Core.Data.Relational` engine: PostgreSQL (Npgsql), MySQL and MariaDB (MySqlConnector —
the MariaDB dialect reads generated keys back with `INSERT … RETURNING`, the capability MySQL
honestly rejects) and SQL Server (Microsoft.Data.SqlClient) differ only in their `SqlDialect`.
All providers ship the same **fluent, typed migrations**, authored with member selectors instead of
field strings — schema creation derived from the model, **live-schema evolution**
(`AddField`/`DropField`), rich index options (typed **filtered/partial indexes**, PostgreSQL
`GIN`, MongoDB `text` and TTL), typed data migrations (`Update`) and a `Run(...)` escape hatch
with direct provider access:

```csharp
services.AddMongoRepositories("mongodb://localhost:27017", "shop");
services.AddMongoMigrations(typeof(AddProductIndexes).Assembly);

[Migration("Product indexes", 2026, 7, 20, 14, 0, 0)]
public sealed class AddProductIndexes : Migration
{
    public override void Up(IMigrationBuilder migration) =>
        migration.For<Product>(product => product
            .EnsureCollection()
            .Index(x => x.Category)
            .Index(x => x.Price, o => o.Filtered(x => x.Quantity > 0))   // partial index, typed predicate
            .CompositeIndex(keys => keys.Descending(x => x.Price).Ascending(x => x.Name))
            .AddField(x => x.Rating)                                     // evolve a live schema
            .DropField("legacy_score"));
}

// on startup: apply pending migrations, once each
await serviceProvider.GetRequiredService<IMigrationRunner>().RunAsync();
```

The typed filtered-index predicate goes through the **same interpretation as query filters** —
rendered to a SQL `WHERE` with inlined literals on PostgreSQL/SQL Server, a partial filter
expression on MongoDB — and every option a store cannot build is rejected with guidance
(MySQL has no filtered indexes, Cassandra points you to `SearchIndex(...)`, Cosmos to its
indexing policy).

### The engine — what the providers add on top of the drivers

- **A complete SQL target (PostgreSQL)** — `OR`/`NOT`/`!=`/`NULL` push down whole with C# semantics
  preserved (`== null` → `IS NULL`, `!=` matches `NULL` rows, `Contains` with a `null` in the list
  matches `NULL` columns), arrays are native (`= ANY`, atomic append/remove), scalar-keyed
  dictionaries are **`jsonb` document columns** (`ContainsKey` pushes down as the `?` operator, the
  indexer — `x.Attributes["tier"] == "gold"` — as `->>`), sorting works on any column and paging is
  native `LIMIT/OFFSET` plus keyset continuation.
- **Pushdown + residual filtering (Cassandra)** — every clause CQL can express runs on the cluster;
  the ones it cannot (`OR` across columns, `!=`, `NULL`, arbitrary predicates) run client-side over
  the fetched rows behind the explicit `.AllowClientEvaluation()` opt-in, with `.AllowFiltering()`
  gating scans. An `OR` whose branches each pin a partition needs no opt-in at all: it runs as
  **parallel native queries**, merged and de-duplicated by primary key. A text column declared with
  `SearchIndex(x => x.Name)` serves `StartsWith`/`EndsWith`/`Contains` and `Db.Like` as native
  `LIKE` on its **SASI index** (the migration creates it) — no scan opt-in: the index serves the
  match.
- **`Explain()` on every provider** (`IExplainableRepository<T>`) — the native statement (CQL,
  Cosmos SQL, aggregation pipeline), what is pushed down versus evaluated client-side, partition
  scoping, and the opt-ins a query still needs. Builds the plan without executing anything.
- **Computed set-based updates** — `UpdateMany(filter, x => new E { N = x.N + 1, Tags = x.Tags.Append("vip").ToList() })`
  renders to the store's atomic form: `$inc`/`$mul`/`$push`/`$addToSet`/`$pullAll` on MongoDB
  (honouring `[BsonElement]` and custom serializers), `PatchOperation.Increment`/`Add` on Cosmos,
  collection `col = col + ?` and **counter columns** on Cassandra. Shapes a store cannot apply
  atomically are rejected with the reason — an update never silently degrades to read-modify-write.
- **Continuation paging and streaming** (`IContinuationReadRepository<T>`,
  `IStreamingReadRepository<T>`) — deep pages walk the native path (Cassandra `PagingState`, Cosmos
  continuation tokens, MongoDB keyset by id), every page costing the same as the first, and
  `IAsyncEnumerable<T>` streams hold one page in memory at a time.
- **Typed `GroupBy` with `HAVING`, and aggregates on the store** (`IGroupedReadRepository<T>`,
  `IAggregateReadRepository<T>`) —
  `GroupByAsync(x => x.Customer, g => new { g.Key, Orders = g.Count(), Revenue = g.Sum(x => x.Total) })`
  renders to a native `GROUP BY` on the relational providers, a `$group` pipeline on MongoDB, a
  primary-key-restricted CQL `GROUP BY` on Cassandra, and a single-member server-side `GROUP BY`
  on Cosmos DB (which has no `HAVING` — the contract says so). The filter pushes before grouping, the
  typed `having` predicate — `g => g.Sum(x => x.Total) > 100 && g.Count() >= 2` — runs as SQL
  `HAVING` / a Mongo `$match` over the groups / against Cassandra's cluster-computed aggregate
  cells (CQL has no `HAVING`; no extra rows travel), and only the grouped rows come back.
  `Min`/`Max`/`Average` push down alongside `Sum`/`Count` on **every** provider. A filter the
  store cannot express degrades — behind `.AllowClientEvaluation()` — to grouping the fetched
  rows with the selectors themselves; a projection shape outside the portable contract is
  rejected with the supported shapes, never silently fetched.
- **Typed `UNION`/`UNION ALL` across entities** (`IUnionQueryRunner`) — compose branches with
  `Union.Of<Order>().Where(x => x.Status == "overdue").Select(x => new { Name = x.Customer, Origin = "order" })`,
  combine with `UnionQuery.All(...)` or `UnionQuery.Distinct(...)`, order and page the combined
  result — one statement on the relational providers, one `$unionWith` aggregation on MongoDB.
  Branches project entity members or constants (tagging which branch a row came from), each
  branch's filter pushes inside its own branch with the entity's global filter applied per branch
  (`IgnoringQueryFilters()` opts a branch out), and a branch the store cannot express is rejected
  with guidance — a union never runs half a branch client-side.
- **Partition-key inference and ETag concurrency (Cosmos)** — a filter that pins the partition key
  scopes the query to a single partition automatically (the biggest RU saving there is), and
  `ConcurrencyToken(x => x.ETag)` turns `Modify` into a conditional `If-Match` replace.
- **Prepared statements and LWT (Cassandra)** — every repeated CQL text is prepared once per
  session and bound thereafter; `AddIfNotExistsAsync` runs `INSERT … IF NOT EXISTS` and reports
  whether it applied; `WithConsistency(...)` sets a per-query consistency level and
  `CommitAsync(o => o.WithTtl(...))` writes expiring rows.
- **Global query filters (multi-tenancy)** — register `new QueryFilters().For<Order>(...)` as a
  singleton and every read *and set-based write* is scoped by it on all three providers; a
  per-request factory receives the scope's `IServiceProvider` (resolve the current tenant there),
  and `IgnoringQueryFilters()` opts a read out. A tenant filter on the Cosmos partition key
  auto-scopes the query to that partition.
- **Entity lifecycle by convention** (`eQuantic.Core.Domain`) — an entity implementing
  `IEntityTimeMark`/`IEntityTimeTrack`/`IEntityTimeEnded` gets `CreatedAt`/`UpdatedAt` stamped on
  every provider (set-based updates included), and its deletes become **soft deletes**:
  `Remove`/`DeleteMany` stamp `DeletedAt`, the row survives, and every read and set-based write is
  scoped to live rows automatically — `IgnoringQueryFilters()` opts a read out. No configuration,
  no hooks to wire.
- **Optimistic concurrency** — `ConcurrencyToken(x => x.Version)` on the relational model makes
  every update and delete match the token it read (`WHERE … AND version = @old`) and bump it; a
  commit that misses rows throws `ConcurrencyConflictException` and rolls back — the lost update
  is caught, never silently overwritten. Cosmos does the same with
  `ConcurrencyToken(x => x.ETag)` as a conditional `If-Match` replace.
- **Value converters (relational)** —
  `.Converts(x => x.Email, email => email.Value, EmailAddress.Create)` maps a Value Object, an
  enum-as-string or any domain type to a stored scalar, applied everywhere the member crosses the
  engine: the DDL column type, inserts and updates (set-based included), filter values and
  materialization. The domain type never leaks to the driver.
- **Declared navigations and nested includes (relational)** —
  `Reference(x => x.Order, x => x.OrderCode)` / `Collection(x => x.Invoices, i => i.OrderCode)`
  override the `{Nav}Id`/`{Entity}Id` conventions for schemas that do not follow them, and a
  dotted include path (`Include("Invoices.Order")`) loads level by level — one `IN` query per
  segment, never a join explosion.
- **Transient-fault retries (relational, opt-in)** — `services.AddRelationalResilience(...)`:
  reads retry automatically on driver-transient failures (`DbException.IsTransient`) with a
  connection reset and exponential backoff; commits retry only behind `RetryCommits` (a lost
  commit acknowledgement may mean the batch applied — the caveat is documented, and concurrency
  tokens make a double-apply loud); nothing retries inside an explicit transaction. Attempts land
  on the span (`equantic.retries`). The document and wide-column drivers ship native retry
  policies, so they need none of this.
- **OpenTelemetry built in** — subscribe to the `eQuantic.Core.Data` `ActivitySource` and every
  provider emits spans with the statement (placeholders, never values) plus the engine's own facts:
  client evaluation, split-query count, partition scoping, staged write counts.

The rule behind all of it: **a query never lies about its cost.** Anything beyond the store's
native access path is explicit and opt-in, and `Explain()` shows exactly what will run. Providers
target `net10.0`.

## Install

```bash
dotnet add package eQuantic.Core.Data
```

Targets `net8.0` and `net10.0`. Depends only on the framework-free
[eQuantic.Linq.Web](https://www.nuget.org/packages/eQuantic.Linq.Web) and
[eQuantic.Linq.Specification](https://www.nuget.org/packages/eQuantic.Linq.Specification).

## Learn more

- [Upgrading to v5.6](docs/UPGRADING-5.6.md) — the behaviour corrections in 5.6.0 and what they
  mean for code written against 5.4/5.5.
- [Repository Pattern walkthrough](Repository.md) — a full example: data entities, unit of work,
  repository, specifications and domain services.
- [v5 contracts design](docs/CONTRACTS_V5_DESIGN.md) — the rationale, the consolidated interface and
  the breaking-change/migration summary.
- [Releasing](docs/releasing.md) — the automated release flow (maintainers).

## Upgrading to v5

v5 is a deliberate breaking redesign: one `QueryOptions` argument per read (not
`Action<Configuration>` + overloads), `PagedResult<T>` paging, the unit-of-work type parameter
removed from `IRepository`/`GetRepository`, an `IEntity<TKey>` constraint (no `new()`), the
SQL/relational surface moved to the provider layer, and the monolithic `eQuantic.Linq` dependency
replaced by `eQuantic.Linq.Web` + `eQuantic.Linq.Specification`. The full before/after is in the
[design doc](docs/CONTRACTS_V5_DESIGN.md#7-breaking-change-summary-what-consumers-must-change).

MIT © eQuantic Tech
