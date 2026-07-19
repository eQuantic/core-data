# eQuantic.Core.Data v5 — Contracts Redesign

> Status: **proposal for review**. The foundation (new value types, project
> settings, dead-dependency removal) is already implemented on the
> `feat/v5-contracts` branch. The breaking interface consolidation described in
> sections 4–7 is **not yet applied** and is presented here for approval before
> the published contract surface is changed.

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
- **`PageRequest`** — one-based `PageIndex` + `PageSize`, with `Skip`/`Take`.
- **`PagedResult<T>`** — `Items`, `TotalCount`, `PageIndex`, `PageSize`,
  `PageCount`, `HasPreviousPage`, `HasNextPage`, `Empty(...)`.
- **`QueryOptions<TEntity>`** — fluent, replaces `Action<Configuration<TEntity>>`:
  `Where(spec)` / `Where(predicate)`, `Include(paths)`, `OrderBy(sortings)`,
  `NoTracking()`, `IgnoringQueryFilters()`, `WithTag(tag)`.
- **Housekeeping fixes (K7):** removed zero-width characters from the GUID regex
  in `IdentityGenerator`; added `[AttributeUsage]` and fixed a parameter-name
  typo on `MigrationAttribute`.

## 4. Proposed consolidated read interface

```csharp
public interface IReadRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
{
    Task<TEntity?> GetByKeyAsync(TKey key, QueryOptions<TEntity>? options = null, CancellationToken ct = default);
    Task<TEntity?> FirstOrDefaultAsync(QueryOptions<TEntity> options, CancellationToken ct = default);
    Task<TEntity?> SingleOrDefaultAsync(QueryOptions<TEntity> options, CancellationToken ct = default);

    Task<IReadOnlyList<TEntity>> ListAsync(QueryOptions<TEntity>? options = null, CancellationToken ct = default);
    Task<IReadOnlyList<TResult>> ListAsync<TResult>(Expression<Func<TEntity, TResult>> selector, QueryOptions<TEntity>? options = null, CancellationToken ct = default);

    Task<PagedResult<TEntity>> GetPagedAsync(PageRequest page, QueryOptions<TEntity>? options = null, CancellationToken ct = default);
    Task<PagedResult<TResult>> GetPagedAsync<TResult>(PageRequest page, Expression<Func<TEntity, TResult>> selector, QueryOptions<TEntity>? options = null, CancellationToken ct = default);

    Task<long> CountAsync(QueryOptions<TEntity>? options = null, CancellationToken ct = default);
    Task<bool> AnyAsync(QueryOptions<TEntity>? options = null, CancellationToken ct = default);
    Task<bool> AllAsync(QueryOptions<TEntity> options, CancellationToken ct = default);

    Task<TResult> SumAsync<TResult>(Expression<Func<TEntity, TResult>> selector, QueryOptions<TEntity>? options = null, CancellationToken ct = default);
}
```

This is **~12 members** replacing ~108. The filter/specification axis collapses
into `QueryOptions.Where(...)`; the config axis collapses into the options
object; the paging axis collapses into `PageRequest` + `PagedResult<T>`; the
numeric `Sum` explosion collapses into a single generic `SumAsync<TResult>`.

### Sync surface

Two options, to decide during review:

- **(A) Async-only.** Drop the synchronous `IReadRepository`/`IWriteRepository`
  mirror entirely. Cleanest, but removes sync callers' entry point.
- **(B) Keep a slimmed sync mirror** with the same consolidated shape.

Recommendation: **(A)** for modern EF Core (which is async-first), unless there
are known synchronous consumers.

## 5. Proposed write interface

```csharp
public interface IWriteRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
{
    Task AddAsync(TEntity item, CancellationToken ct = default);
    Task AddRangeAsync(IEnumerable<TEntity> items, CancellationToken ct = default);
    void Update(TEntity item);
    void Remove(TEntity item);
    Task<long> UpdateManyAsync(QueryOptions<TEntity> options, Expression<Func<TEntity, TEntity>> update, CancellationToken ct = default);
    Task<long> RemoveManyAsync(QueryOptions<TEntity> options, CancellationToken ct = default);
}
```

`Merge`, `Modify`, `TrackItem`, `Attach` collapse into `Update`; bulk operations
take `QueryOptions` for a uniform predicate.

## 6. Entity, UnitOfWork and SQL surface

- **`IEntity<TKey>`** becomes the repository constraint. `IEntity` (marker) is
  kept for non-keyed scenarios and for backwards source-compatibility of the
  namespace.
- **`new()` constraint removed** — entity construction becomes the provider's
  responsibility.
- **`IUnitOfWork`** keeps `Commit`/`CommitAsync`/`Rollback` but its
  `GetRepository<TUnitOfWork, …>()` generic methods drop the `TUnitOfWork`
  parameter: `GetRepository<TEntity, TKey>()`.
- **`ISqlUnitOfWork` and the relational/EF-only members** are candidates to move
  out of the agnostic contracts into the EF package (or a dedicated
  `eQuantic.Core.Data.Relational.Abstractions`). Decision needed in review.

## 7. Breaking-change summary (what consumers must change)

| v4 | v5 |
|----|----|
| `repo.GetAllAsync(c => c.WithNoTracking())` | `repo.ListAsync(new QueryOptions<T>().NoTracking())` |
| `repo.GetFilteredAsync(f, c => …)` | `repo.ListAsync(new QueryOptions<T>().Where(f)…)` |
| `repo.GetPagedAsync(i, size)` → `IEnumerable<T>` | `repo.GetPagedAsync(PageRequest.Of(i, size))` → `PagedResult<T>` |
| `repo.CountAsync(spec)` | `repo.CountAsync(new QueryOptions<T>().Where(spec))` |
| `IRepository<TUnitOfWork, TEntity, TKey>` | `IRepository<TEntity, TKey>` |
| `where TEntity : class, IEntity, new()` | `where TEntity : class, IEntity<TKey>` |
| `Sum(x => x.Amount)` (per-numeric-type overloads) | `SumAsync(x => x.Amount)` (generic) |

## 8. Open decisions for review

1. **Sync mirror:** drop entirely (A) or keep slimmed (B)?
2. **SQL/relational surface:** move `ISqlUnitOfWork` out of the contracts
   package, or keep it here behind the agnostic core?
3. **Method naming:** `ListAsync` vs. `GetAllAsync`/`FindAsync`;
   `FirstOrDefaultAsync` vs. `GetFirstAsync`. Keeping familiar names lowers the
   migration cost even in a major version.
4. **`IEntity` marker:** keep for compatibility, or require `IEntity<TKey>`
   everywhere?

Once these are settled, the consolidation is applied to the interfaces here and
the EF package is reimplemented against the new surface.
