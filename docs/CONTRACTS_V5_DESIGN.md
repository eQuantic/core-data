# eQuantic.Core.Data v5 — Contracts Redesign

> Status: **applied** on the `feat/v5-contracts` branch. The foundation (new
> value types, project settings, dead-dependency removal) and the breaking
> interface consolidation described below are both implemented. The contracts
> package builds clean (0 warnings, 0 errors) across net8.0 and net10.0. The Entity
> Framework provider packages are reimplemented against this surface as a
> separate follow-up.
>
> Decisions taken during review (see §8): keep a slimmed **synchronous mirror**;
> **move the SQL/relational surface out** of the contracts package; **keep the
> familiar method names** (`GetAllAsync`, `GetFirstAsync`, `GetPagedAsync`, …);
> keep the per-numeric-type `Sum`/`SumAsync` overloads.

## 1. Why v5

`eQuantic.Core.Data` has been published for years and its contract surface has
grown organically. The read repository interfaces alone expose roughly **100
members per side** (sync and async), almost all of which are mechanical
overloads of a handful of real operations. Concretely, today we ship:

| Interface | Approx. members | Nature |
|-----------|-----------------|--------|
| `IAsyncReadRepository<TConfig, TEntity, TKey>` | ~108 | `Count`/`Sum`/`All`/`Any`/`Get*`/`GetPaged*` each multiplied by `{filter, specification}` × `{config, config+ct, ct}` |
| `IReadRepository<TConfig, TEntity, TKey>` | ~85 | same, without the `CancellationToken` axis |
| `ISqlUnitOfWork` | ~30 | EF-/relational-specific operations living in the contracts package |

The multiplication comes from three independent decisions repeated across every
method:

1. **Filter vs. specification** — two ways to express the same predicate.
2. **`Action<TConfig> configuration`** — an optional shaping callback, which
   forces `(config)`, `(config, ct)` and `(ct)` variants because a default
   parameter cannot be combined with a trailing `CancellationToken` cleanly.
3. **Paging shape** — `GetPagedAsync` is offered as `(limit)`, `(pageIndex,
   pageSize)`, each crossed with filter/specification/config/ct, and it returns
   a bare `IEnumerable<TEntity>` with **no total count**, so callers cannot build
   a real paged UI without a second `CountAsync` round-trip.

On top of the overload explosion there are structural issues:

- **`TUnitOfWork` is threaded through the type parameters** of the repository
  interfaces (`IRepository<TUnitOfWork, TEntity, TKey>`,
  `IAsyncReadRepository<TUnitOfWork, TConfig, TEntity, TKey>`), leaking the
  persistence engine into every consumer signature.
- **`IEntity` is an empty marker** and `TKey` is an unrelated second type
  parameter, so nothing ties an entity to its own key type. `IEntity<TKey>`
  exists but is not used by the constraints.
- **`new()` constraint** on `TEntity` is required everywhere, forcing every
  entity to have a public parameterless constructor.
- **EF-specific surface** (`ISqlUnitOfWork`, `LoadCollection`,
  `UpdateDatabase`, `GetPendingMigrations`, …) lives in the provider-agnostic
  contracts package.

v5 is the opportunity to fix these in one deliberate breaking release.

## 2. Design principles

1. **One method per operation.** Filtering, shaping, sorting, includes and
   tracking move into a single options object, so each operation is a single
   method with an optional `QueryOptions<TEntity>` and a trailing
   `CancellationToken`.
2. **Real paging.** Paged reads return `PagedResult<T>` (items + total count +
   page metadata) instead of a bare sequence.
3. **The entity owns its key.** `IEntity<TKey>` becomes the constraint used by
   the repository, removing the free-floating `TKey` mismatch.
4. **Persistence engine stays out of the signatures.** `TUnitOfWork` is removed
   from repository type parameters.
5. **Contracts stay provider-agnostic.** EF/relational-only surface is not part
   of this package's core contracts.
6. **Modern C#.** `Nullable` reference types and XML documentation are enabled;
   read results are `IReadOnlyList<T>`.

## 3. Foundation already implemented (non-breaking)

These are additive and already on the branch:

- **Project settings** (`eQuantic.Core.Data.csproj`): version `5.0.0`,
  `<Nullable>enable</Nullable>`, `<GenerateDocumentationFile>true</…>`, and the
  removal of the **unused** `eQuantic.Core 1.8.4` package reference (confirmed no
  `.cs` file references it).
- **Dependency migration.** The discontinued monolithic `eQuantic.Linq 2.1.0` is
  replaced by the refactored collection: `eQuantic.Linq.Specification 3.7.0` (the
  specification pattern) and `eQuantic.Linq.Web 3.7.0` (the query DSL that
  provides `QueryFilter`/`QuerySort` parsing and the typed
  `QueryFilterBuilder<T>`/`QuerySortBuilder<T>` fluent builders). `eQuantic.Linq.Web`
  depends only on `eQuantic.Linq.Expressions`, not on ASP.NET.
- **Target frameworks.** Standardized on `net8.0` and `net10.0`, aligned with the
  rest of the eQuantic family. The end-of-life `net6.0`/`net7.0` and the
  out-of-support `net9.0` targets are dropped.
- **`PageRequest`** — one-based `PageIndex` + `PageSize`, with `Skip`/`Take`.
- **`PagedResult<T>`** — `Items`, `TotalCount`, `PageIndex`, `PageSize`,
  `PageCount`, `HasPreviousPage`, `HasNextPage`, `Empty(...)`.
- **`QueryOptions<TEntity>`** — fluent, replaces `Action<Configuration<TEntity>>`:
  `Where(spec)` / `Where(predicate)` / `Where("name:eq(John)")` (string filter via
  `eQuantic.Linq.Web`), `Include(paths)`, `OrderBy("total:desc,customer.name")`
  (string ordering) / `OrderBy(params QuerySort<TEntity>[])`, `NoTracking()`,
  `IgnoringQueryFilters()`, `WithTag(tag)`, `WithBeforeCustomization`/`WithAfterCustomization`.
- **Housekeeping fixes (K7):** removed zero-width characters from the GUID regex
  in `IdentityGenerator`; added `[AttributeUsage]` and fixed a parameter-name
  typo on `MigrationAttribute`.

## 4. Consolidated read interface (as implemented)

Familiar names are retained; each operation is a single method that takes an
optional `QueryOptions<TEntity>` (filtering, sorting, includes, tracking) and a
trailing `CancellationToken`. Paged reads return `PagedResult<T>`.

```csharp
public interface IAsyncReadRepository<TEntity, TKey> : IAsyncRepository
    where TEntity : class, IEntity<TKey>
{
    Task<TEntity?> GetAsync(TKey id, QueryOptions<TEntity>? options = null, CancellationToken ct = default);
    Task<IEnumerable<TEntity>> GetAllAsync(QueryOptions<TEntity>? options = null, CancellationToken ct = default);
    Task<IEnumerable<TEntity>> GetFilteredAsync(Expression<Func<TEntity, bool>> filter, QueryOptions<TEntity>? options = null, CancellationToken ct = default);
    Task<IEnumerable<TEntity>> AllMatchingAsync(ISpecification<TEntity> specification, QueryOptions<TEntity>? options = null, CancellationToken ct = default);
    Task<IEnumerable<TResult>> GetMappedAsync<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null, CancellationToken ct = default);
    Task<TEntity?> GetFirstAsync(QueryOptions<TEntity> options, CancellationToken ct = default);
    Task<TResult?> GetFirstMappedAsync<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity> options, CancellationToken ct = default);
    Task<TEntity?> GetSingleAsync(QueryOptions<TEntity> options, CancellationToken ct = default);
    Task<PagedResult<TEntity>> GetPagedAsync(PageRequest page, QueryOptions<TEntity>? options = null, CancellationToken ct = default);
    Task<PagedResult<TResult>> GetPagedAsync<TResult>(PageRequest page, Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null, CancellationToken ct = default);
    Task<long> CountAsync(QueryOptions<TEntity>? options = null, CancellationToken ct = default);
    Task<bool> AnyAsync(QueryOptions<TEntity>? options = null, CancellationToken ct = default);
    Task<bool> AllAsync(Expression<Func<TEntity, bool>> predicate, QueryOptions<TEntity>? options = null, CancellationToken ct = default);
    // SumAsync: one overload per numeric type (int, int?, long, long?, double, double?, float, float?, decimal, decimal?)
    Task<decimal> SumAsync(Expression<Func<TEntity, decimal>> selector, QueryOptions<TEntity>? options = null, CancellationToken ct = default);
    // ...
}
```

The filter/specification axis collapses into `QueryOptions.Where(...)`; the
config axis collapses into the options object; the paging axis collapses into
`PageRequest` + `PagedResult<T>`. `IAsyncReadRepository` drops from ~108 members
to ~23 (13 read operations + 10 `Sum` overloads).

### Sync surface

A **slimmed synchronous mirror** (`IReadRepository<TEntity, TKey>`) is retained
with the same shape, minus the `CancellationToken` axis.

## 5. Write interface (as implemented)

The familiar write operations are retained; `AddRange`/`AddRangeAsync` are added.
Bulk operations keep their `filter` / `specification` overloads (two per
operation — no overload explosion). The `TUnitOfWork` arity is removed.

```csharp
public interface IAsyncWriteRepository<TEntity> : IAsyncRepository
    where TEntity : class, IEntity
{
    Task AddAsync(TEntity item, CancellationToken ct = default);
    Task AddRangeAsync(IEnumerable<TEntity> items, CancellationToken ct = default);
    Task<long> DeleteManyAsync(Expression<Func<TEntity, bool>> filter, CancellationToken ct = default);
    Task<long> DeleteManyAsync(ISpecification<TEntity> specification, CancellationToken ct = default);
    Task MergeAsync(TEntity persisted, TEntity current);
    Task ModifyAsync(TEntity item);
    Task RemoveAsync(TEntity item);
    Task<long> UpdateManyAsync(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateFactory, CancellationToken ct = default);
    Task<long> UpdateManyAsync(ISpecification<TEntity> specification, Expression<Func<TEntity, TEntity>> updateFactory, CancellationToken ct = default);
}
```

The synchronous `IWriteRepository<TEntity>` mirrors this and additionally exposes
`TrackItem`.

## 6. Entity, UnitOfWork and SQL surface (as implemented)

- **`IEntity<TKey>`** is now the repository constraint. `IEntity` (marker) is
  kept for the write side and non-keyed scenarios.
- **`new()` constraint removed** — entity construction becomes the provider's
  responsibility.
- **`IUnitOfWork`** keeps `Commit`/`CommitAsync`/`RollbackChanges`; its
  `GetRepository`/`GetAsyncRepository` generic methods drop the `TUnitOfWork`
  parameter: `GetRepository<TEntity, TKey>()`. `IQueryableUnitOfWork` does the
  same for `GetQueryableRepository`/`GetAsyncQueryableRepository` and
  `CreateSet<TEntity>()`.
- **SQL/relational surface moved out.** `ISqlUnitOfWork`, `ISqlExecutor`,
  `IAsyncSqlExecutor`, `ISqlRepository`, `ParamValue` and the `SqlConfiguration`
  hierarchy were removed from this package. They will be re-homed in the EF
  provider layer during its reimplementation, keeping the contracts
  provider-agnostic.
- **`Configuration`/`QueryableConfiguration`** are removed; `QueryOptions<TEntity>`
  replaces them (it also absorbed `WithBeforeCustomization`/`WithAfterCustomization`).

## 7. Breaking-change summary (what consumers must change)

| v4 | v5 |
|----|----|
| `repo.GetAllAsync(c => c.WithNoTracking())` | `repo.GetAllAsync(new QueryOptions<T>().NoTracking())` |
| `repo.GetFilteredAsync(f, c => …)` | `repo.GetFilteredAsync(f, new QueryOptions<T>()…)` |
| `repo.GetFirstAsync(f)` | `repo.GetFirstAsync(new QueryOptions<T>().Where(f))` |
| `repo.GetPagedAsync(i, size)` → `IEnumerable<T>` | `repo.GetPagedAsync(PageRequest.Of(i, size))` → `PagedResult<T>` |
| `repo.CountAsync(spec)` | `repo.CountAsync(new QueryOptions<T>().Where(spec))` |
| `IRepository<TUnitOfWork, TEntity, TKey>` | `IRepository<TEntity, TKey>` |
| `where TEntity : class, IEntity, new()` | `where TEntity : class, IEntity<TKey>` |
| `uow.GetRepository<TUoW, T, TKey>()` | `uow.GetRepository<T, TKey>()` |
| `ISqlUnitOfWork` (from contracts) | provided by the EF provider layer |

## 8. Decisions taken during review

1. **Sync mirror:** kept, slimmed to the consolidated shape.
2. **SQL/relational surface:** moved out of the contracts package (to the EF
   provider layer).
3. **Method naming:** familiar names kept (`GetAllAsync`, `GetFirstAsync`,
   `GetSingleAsync`, `GetPagedAsync`, `GetMappedAsync`, `CountAsync`, `AnyAsync`,
   `AllAsync`, `SumAsync`).
4. **`Sum` overloads:** the per-numeric-type overloads are kept (consolidated to
   a single `QueryOptions` argument), as EF Core translates them directly.
5. **`IEntity` marker:** kept for the write side; `IEntity<TKey>` is required by
   the keyed read/repository interfaces.

The contracts consolidation is implemented on `feat/v5-contracts`. The remaining
follow-up is reimplementing the Entity Framework provider packages against this
surface (and re-homing the SQL/relational abstractions there).
