# Cookbook — coming from Entity Framework

A translation table and the mental-model shifts. The APIs map closely; the philosophy differs in
three places, all deliberate.

## API translation

| EF Core | eQuantic.Core.Data |
|---|---|
| `DbContext` | `IUnitOfWork` (per scope, from DI) |
| `DbSet<T>` | `IAsyncRepository<T, TKey>` (open generic, from DI) |
| `dbSet.Add(e)` / `Remove(e)` | `repo.AddAsync(e)` / `RemoveAsync(e)` |
| mutate + `SaveChanges()` | `repo.ModifyAsync(e)` + `uow.CommitAsync()` — **intent is explicit** |
| `Where(...).OrderBy(...).Skip/Take` | `QueryOptions<T>().Where(...).OrderBy(...)` + `GetPagedAsync(new PageRequest(i, n))` |
| `FirstOrDefaultAsync` / `SingleOrDefaultAsync` | `GetFirstAsync(options)` / `GetSingleAsync(options)` |
| `FindAsync(id)` | `GetAsync(id)` |
| `Include(x => x.Nav).ThenInclude(...)` | `.Include("Nav")` / `.Include("Nav.Child")` |
| `ExecuteUpdateAsync` / `ExecuteDeleteAsync` | `UpdateManyAsync(filter, factory)` / `DeleteManyAsync(filter)` |
| `HasQueryFilter(...)` | `QueryFilters.For<T>(...)` (per-scope factories included) |
| `[Column("name")]` / `HasColumnName` | `[StoredAs("name")]` / `.Column(x => …, "name")` |
| `HasKey(x => new { a, b })` | `.Key(x => new { a, b })` |
| `IsRowVersion()` / `IsConcurrencyToken()` | `[ConcurrencyToken]` / `.ConcurrencyToken(x => …)` |
| `HasConversion(...)` | `.Converts(x => …, toStored, fromStored)` |
| `HasMaxLength` / `HasPrecision` | `[Facet(Length = …)]` / `.Facet(x => …, precision: , scale: )` |
| `DbUpdateConcurrencyException` | `ConcurrencyConflictException` (reload, reapply, retry) |
| `Migrate()` / `Add-Migration` | timestamped `Migration` classes + `IMigrationRunner.RunAsync()` |
| `context.Database.BeginTransaction()` | `uow.BeginTransactionAsync()` |

## The three mental-model shifts

**1. No change tracking.** There is no tracker to notice your mutations — you say `ModifyAsync`.
This is the [lean write model](../concepts/write-model.md): predictable writes, no snapshot cost,
entities as plain objects. If a code path "just mutated and saved", it now states its intent — that
is a migration task, and usually a clarity win in review.

**2. No lazy loading.** Navigations load when you `Include` them, with one follow-up `IN` query
each — never a property-getter query storm. Code that relied on lazy loading gets explicit about
what it needs, which is where N+1 bugs go to die.

**3. Nothing silent.** EF's LINQ accepts almost anything and decides for you what happens
(translate, throw, or historically, evaluate client-side). Here the
[gates](../querying/pushdown.md) make the decision *yours*: what cannot push down refuses with
guidance, and running it in memory is an explicit `.AllowClientEvaluation()` at the call site.

## What you gain on the way

- The same contracts on MongoDB, Cosmos DB and Cassandra — with each store's native mechanisms
  (partition pinning, `$unionWith`, LWTs) instead of a relational emulation.
- `Explain()` on queries **and models** — testable plans, testable mappings.
- Typed `GroupByAsync` with HAVING, typed `UNION`, continuation-token paging, streaming.
- A modeling vocabulary with no driver attributes and a documented per-store matrix.

## Porting checklist

1. Entities: implement `IEntity<TKey>` (or the DataModel bases); replace EF attributes with the
   [vocabulary](../modeling/annotations.md).
2. Model: move `OnModelCreating` content into the provider's fluent builder at registration.
3. Repositories: replace `DbSet` usage with the injected repositories; add `ModifyAsync` where code
   relied on tracking.
4. Queries: move predicates into `QueryOptions`/specifications; make includes explicit.
5. Schema: recreate the current schema as a first `EnsureCollection()`-based migration.
6. Tests: the suites in this repository test against **real stores in containers** — the same
   approach (Testcontainers) is the recommended pattern for yours.
