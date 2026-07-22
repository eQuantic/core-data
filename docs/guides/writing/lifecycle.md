# Lifecycle — stamps and soft delete

Timestamps, soft deletes and user attribution are **conventions over interfaces**, applied at
write-staging time so they behave identically on all six stores. Configuration lives in
[`DataConventions`](../modeling/conventions.md) — including the `Clock`, so time is fakeable.

## Timestamps

| Interface (`eQuantic.Core.Domain`) | Member | Stamped |
|---|---|---|
| `IEntityTimeMark` | `CreatedAt` | once, on first persist |
| `IEntityTimeTrack` | `UpdatedAt?` | on every write |
| `IEntityTimeEnded` | `DeletedAt?` | on `Remove` — which becomes a soft delete |

```csharp
public sealed class Document : IEntity<Guid>, IEntityTimeMark, IEntityTimeTrack, IEntityTimeEnded
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public Guid GetKey() => Id;
    public void SetKey(Guid key) => Id = key;
}
```

The `eQuantic.Core.DataModel` base classes (`EntityDataBase` → `EntityOwnedDataBase` →
`EntityTrackDataBase` → `EntityHistoryDataBase`) implement the stack incrementally — use them or
implement the interfaces directly; the engine only sees the interfaces.

## Soft delete — three coordinated behaviors

Implementing `IEntityTimeEnded` changes three things at once, because a soft delete that only
changes the write is a bug factory:

1. **`Remove` stamps instead of deleting.** `DeletedAt = now` (+ `DeletedById` when configured) and
   the row/document survives, staged as an update.
2. **Reads filter automatically.** Every read on the entity gets `DeletedAt == null` ANDed in — the
   same machinery as [global query filters](../concepts/contracts.md#global-query-filters). Reading
   deleted rows is a per-query decision: `.IgnoringQueryFilters()`.
3. **Set-based deletes convert.** `DeleteManyAsync(filter)` becomes a set-based *update* stamping
   `DeletedAt` (+ attribution) — still one server-side statement, still no entity loading.

Turn the whole behavior off globally with `DataConventions.SoftDelete = false` (hard deletes
everywhere), or bypass per call site by querying with filters ignored and staging explicit state.

## WHO attribution

With `DataConventions.CurrentUserId` configured, properties named `CreatedById` / `UpdatedById` /
`DeletedById` are stamped at the corresponding moments — resolved **per request** through the
scope's `IServiceProvider`, so the accessor can read authentication state:

```csharp
services.AddSingleton(new DataConventions
{
    CurrentUserId = sp => (sp.GetService(typeof(ICurrentUser)) as ICurrentUser)?.Id,
});
```

No interface is required for attribution — the property name is the convention — but the
`eQuantic.Core.DataModel` who-interfaces give the properties their canonical, typed shape. The
full recipe (including reading the stamps back for audit trails) is in
[Auditing](../cookbook/auditing.md).

## Semantics worth knowing

- Stamps use `DataConventions.Clock` (a `TimeProvider`) — inject `FakeTimeProvider` in tests and
  lifecycle tests become deterministic.
- `CreatedAt` stamps only when unset, so importing historical data with explicit values works.
- On upsert-native stores (Cassandra), `Add` and `Modify` both stamp correctly — `CreatedAt` when
  unset, `UpdatedAt` always — matching the store's real upsert semantics.
- Lifecycle stamping composes with [concurrency tokens](concurrency.md): stamp first, then the
  conditional write carries both.
