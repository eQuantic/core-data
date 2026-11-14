# The contracts

Everything in eQuantic.Core.Data hangs off a small set of interfaces in
`eQuantic.Core.Data.Repository`. The persistence engine is deliberately **kept out of the type
signatures**: it is `IRepository<TEntity, TKey>`, not `IRepository<TUnitOfWork, TEntity, TKey>` —
application code compiled against the contracts does not know (or care) which store implements them.

## The repository family

| Interface | What it adds |
|---|---|
| `IReadRepository<TEntity, TKey>` / `IAsyncReadRepository<…>` | Point reads (`Get`), filtered reads (`GetFiltered`, `AllMatching`), single/first, paging (`GetPaged`), `Count`/`Any`/`All` |
| `IRepository<TEntity, TKey>` / `IAsyncRepository<…>` | Writes: `Add`, `Modify`, `Merge`, `Remove`, plus set-based `UpdateMany`/`DeleteMany` |
| `IQueryableRepository<…>` / `IAsyncQueryableRepository<…>` | The same, constrained to a queryable unit of work |
| `IAggregateReadRepository<TEntity>` | `Sum`, `Min`, `Max`, `Average` — pushed down where the store can |
| `IGroupedReadRepository<TEntity>` | Typed `GroupByAsync` with aggregates and `HAVING` |
| `IDistinctReadRepository<TEntity>` | `GetDistinctAsync` — the distinct values of one member, or combinations of several |
| `IContinuationReadRepository<TEntity>` | `GetPageAsync(pageSize, continuationToken)` — keyset/native continuation paging |
| `IStreamingReadRepository<TEntity>` | `GetStreamAsync` — `IAsyncEnumerable<TEntity>`, one page in memory at a time |
| `IExplainableRepository<TEntity>` | `Explain(options)` — the query plan, before you run it |
| `IUnionQueryRunner` | `UnionAsync` — typed UNION / UNION ALL across entities |
| `IUpsertRepository<TEntity, TKey>` | `UpsertAsync` (on the key or a unique key) and `InsertManyAsync(…, skipDuplicates)` — see [Upserts](../writing/upserts.md) |
| `IReturningUpdateRepository<TEntity, TKey>` | `UpdateAndGetAsync` — a set-based update that returns the rows it wrote |
| `IPartialModifyRepository<TEntity, TKey>` | `Modify`/`ModifyAsync(entity, members)` — a staged update of the named members only |

A repository instance implements all of these that its provider supports; cast to the capability
you need (`(IAggregateReadRepository<Product>)repo`). The write capabilities, keyed like the
repositories by entity and key, also resolve from DI (`IUpsertRepository<Secret, Guid>`), through the
open-generic registration and the per-entity NativeAOT one alike.

**You rarely write a repository class.** The DI extensions register open generics, so
`IAsyncRepository<Product, Guid>` resolves for any entity in the model. Subclass the provider's
repository only to add domain-specific query methods.

## The unit of work

| Interface | What it is |
|---|---|
| `IUnitOfWork` | `Commit`/`CommitAsync`, `RollbackChanges`, repository factories |
| `IQueryableUnitOfWork` | The provider-backed unit of work repositories are built over |
| `ILockingUnitOfWork` | `AcquireLockAsync(name, timeout)` — a named lock, held until its handle is disposed or, inside a transaction, until the transaction ends; every provider, each through its own mechanism — see [Named locks](../writing/locks.md) |

Writes **stage** on the unit of work and flush on `Commit` — see
[The lean write model](write-model.md). One unit of work per scope (per request in a web app) is
the intended lifetime, and the DI extensions register exactly that.

## The query surface: `QueryOptions<TEntity>`

Every read takes an optional `QueryOptions<TEntity>` — a small, serializable description of the
query around the method's own arguments:

```csharp
var options = new QueryOptions<Product>()
    .Where(p => p.Category == "Peripherals")           // typed lambda…
    .Where(p => p.Price, FilterOperator.LessThan, 30m) // …typed member + operator…
    .Where("price", FilterOperator.LessThan, 30)       // …path + operator (wire-friendly)…
    .Where(specification)                              // …or a specification
    .OrderBy(p => p.Price).ThenByDescending(p => p.Name)
    .ThenBy(p => p.Discontinued, NullsOrder.Last)      // nulls first or last, on every store
    .Skip(20).Take(10)                                 // a page, no count
    .Include(nameof(Product.Supplier))                 // navigations, on every store
    .Include(p => p.Reviews, r => r                    // …or a collection, shaped per parent
        .Where(x => x.Approved).OrderByDescending(x => x.At).Take(3))
    .IgnoringQueryFilters()                      // opt out of global filters
    .WithTag("catalog-page");                    // lands in traces / query text
```

Provider packages add store-specific opt-ins as extension methods —
`.AllowFiltering()` / `.AllowClientEvaluation()` / `.WithConsistency(…)` on Cassandra, for example.
They compose on the same options object, so the *shape* of a query stays portable and the
*store-specific costs* stay visible at the call site.

Paging types are equally small: `PageRequest(pageIndex, pageSize)` in, `PagedResult<T>` (items +
total count) out; `ContinuedResult<T>` (items + opaque continuation token) for token paging. The
read shapes — [shaped includes](../querying/includes.md#shaping-an-included-collection),
[relation counts](../querying/projections.md#counting-a-relation),
[nulls placement and pages without a total](../querying/paging.md#where-nulls-go) and
[distinct values](../querying/aggregates.md#distinct-values) — mean the same on every store, and
each page says which mechanism each store uses.

## Global query filters

`QueryFilters` registers per-entity predicates that AND themselves into every read — the mechanism
behind multi-tenancy and soft-delete filtering:

```csharp
services.AddSingleton(new QueryFilters()
    .For<Order>(scope =>
        scope.GetService(typeof(ITenantAccessor)) is ITenantAccessor tenant
            ? order => order.TenantId == tenant.Id
            : null));
```

The factory overload runs **per scope**, so the filter can read the current request. Opt out per
query with `.IgnoringQueryFilters()`. Soft-delete entities get their live-rows filter automatically
— see [Lifecycle](../writing/lifecycle.md).

## Entities

An entity implements `IEntity<TKey>` (`GetKey`/`SetKey`). No base class, no tracking proxies, no
required parameterless-constructor gymnastics. The interface lives in `eQuantic.Core.DataModel`
(and is type-forwarded from this package), so entities can be shared with the wider eQuantic stack
— `EntityDataBase`, `EntityHistoryDataBase` and friends all satisfy it.
