# Upgrading to v5.6

v5.6.0 is additive in API surface, but it also **corrects provider behaviour** that previous
versions got wrong or left silent. If you upgrade from 5.4/5.5, read the list below — the fixes are
deliberate, and a few of them can surface as new exceptions in code that (unknowingly) relied on
the old behaviour.

## Behaviour changes

1. **`GetFiltered`/`AllMatching` compose with the options filter.** On Cassandra these used to
   *replace* `QueryOptions.Filter` with the argument predicate (and mutated the caller's options).
   All providers now agree: both filters apply (`AND`), and the options instance is never mutated.

2. **`Get`/`GetAsync` honour `QueryOptions` on Cassandra.** The point lookup used to ignore the
   options entirely. It now applies their filter/sorting — which also means a non-key options
   filter without `.AllowFiltering()` now throws `NotSupportedException` (with guidance) instead of
   being silently dropped.

3. **`Include(...)` throws on Cosmos and Cassandra.** It used to be silently ignored. Both stores
   have no cross-document navigations; the exception says so. MongoDB keeps its server-side
   `$lookup` support.

4. **`NULL` comparisons no longer reach Cassandra as invalid CQL.** `x => x.Name == null` used to
   fail server-side; it now either runs client-side behind `.AllowClientEvaluation()` or fails fast
   client-side with guidance.

5. **Predicates Cassandra cannot express are gated, not rejected.** `OR` across columns, `!=` and
   arbitrary predicates used to throw unconditionally. They now run behind explicit opt-ins
   (`.AllowClientEvaluation()`, plus `.AllowFiltering()` for unscoped fetches) — and a
   partition-pinned `OR` runs natively as parallel split queries with **no opt-in**. Code that
   *caught* the old exceptions as a feature probe should use `Explain()` instead.

6. **`CosmosReadRepository.Query` (protected) changed signature** from
   `Func<IQueryable<T>, IQueryable<T>>` to `Expression<Func<T, bool>>?` so the extra predicate can
   feed partition-key inference. Only derived repositories that called it are affected.

7. **`.AllowFiltering()` no longer rides `QueryOptions.Tag`.** The diagnostic tag is free for your
   own use again; combining `WithTag(...)` and `AllowFiltering()` now works.

8. **Cassandra statements are prepared.** The first execution of each statement shape pays one
   prepare round-trip; every execution after that is bound from the per-session cache. Deployments
   with statement-level audit tooling will see `?` placeholders instead of inline literals.

9. **MongoDB reads join the open transaction session.** A read inside `BeginTransactionAsync`
   now sees the transaction's own (flushed) writes, and an aborted transaction hides them.

10. **`UpdateMany` computed shapes now work** (`x => new E { N = x.N + 1, Tags = x.Tags.Append(...) }`)
    and render as native atomic operations. Shapes no store can apply atomically are still
    rejected — with the supported list in the message.

## Additive (no action needed unless you opt in)

- `Explain()` (`IExplainableRepository<T>`), continuation paging (`IContinuationReadRepository<T>`),
  streaming (`IStreamingReadRepository<T>`).
- Global query filters (`QueryFilters` singleton; per-request factories via the scope's
  `IServiceProvider`; `IgnoringQueryFilters()` opts a read out — set-based writes never opt out).
- Cosmos ETag concurrency (`ConcurrencyToken(x => x.ETag)`): once declared, a `Modify` of an entity
  carrying its `_etag` becomes a conditional replace and a concurrent change fails the commit with
  a `PreconditionFailed` `CosmosException`.
- Cassandra counter columns (`Counter(x => x.Hits)`), `AddIfNotExistsAsync` (LWT),
  `WithConsistency(...)` per query, and `CommitAsync(o => o.WithConsistency(...).WithTtl(...))`.
- OpenTelemetry: subscribe to the `eQuantic.Core.Data` `ActivitySource`.

## Worth knowing (unchanged, now documented)

- Outside an explicit transaction, `Commit` flushes staged writes **concurrently**; a failed write
  does not undo the others. Use `BeginTransactionAsync`/`CommitTransactionAsync` when you need
  atomicity (MongoDB session, Cosmos single-partition batch, Cassandra `LOGGED BATCH`).
- The synchronous repository members delegate to the asynchronous ones
  (`GetAwaiter().GetResult()`); prefer the async surface in contexts that have a
  `SynchronizationContext`.
