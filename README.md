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

## Native document-store provider (MongoDB)

`eQuantic.Core.Data.MongoDb` implements the same contracts **directly on the MongoDB driver — no
Entity Framework**. The write model is lean: `Add`/`Modify`/`Remove` buffer typed write models and a
single ordered bulk write runs on `Commit` (no change tracking or snapshotting); explicit multi-document
transactions are opt-in. It also brings what EF's document support cannot — **fluent, typed migrations**
for a document store, authored with member selectors instead of field strings:

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
            .CompositeIndex(keys => keys.Descending(x => x.Price).Ascending(x => x.Name)));
}

// on startup: apply pending migrations, once each
await serviceProvider.GetRequiredService<IMigrationRunner>().RunAsync();
```

Targets `net10.0`. Set-based `UpdateMany(filter, x => new Product { Status = "Closed" })` translates the
member-init to a `$set`, honouring `[BsonElement]`/`[BsonRepresentation]` and custom serializers.

## Install

```bash
dotnet add package eQuantic.Core.Data
```

Targets `net8.0` and `net10.0`. Depends only on the framework-free
[eQuantic.Linq.Web](https://www.nuget.org/packages/eQuantic.Linq.Web) and
[eQuantic.Linq.Specification](https://www.nuget.org/packages/eQuantic.Linq.Specification).

## Learn more

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
