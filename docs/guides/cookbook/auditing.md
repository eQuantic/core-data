# Cookbook — auditing: who and when

Full write attribution — created/updated/deleted, by whom, at what time — from conventions alone.
No interceptors to write, no per-repository code, identical on all six stores.

## The recipe

```csharp
// 1. The entity: DataModel base classes give the canonical shape…
public sealed class Contract : EntityHistoryDataBase, IEntity<Guid>
{
    public string Title { get; set; } = "";
    // inherits: CreatedAt/CreatedById, UpdatedAt/UpdatedById, DeletedAt/DeletedById
}

// …or implement the interfaces + named properties directly (same effect):
public sealed class Contract : IEntity<Guid>, IEntityTimeMark, IEntityTimeTrack, IEntityTimeEnded
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public DateTime CreatedAt { get; set; }     public Guid? CreatedById { get; set; }
    public DateTime? UpdatedAt { get; set; }    public Guid? UpdatedById { get; set; }
    public DateTime? DeletedAt { get; set; }    public Guid? DeletedById { get; set; }
    public Guid GetKey() => Id;
    public void SetKey(Guid key) => Id = key;
}
```

```csharp
// 2. The conventions — the clock and the current user, resolved per request:
services.AddSingleton(new DataConventions
{
    Clock = TimeProvider.System,
    CurrentUserId = sp => (sp.GetService(typeof(ICurrentUser)) as ICurrentUser)?.Id,
});
```

## What now happens, everywhere

| Operation | Stamps |
|---|---|
| `Add` + commit | `CreatedAt`, `CreatedById` |
| `Modify`/`Merge` + commit | `UpdatedAt`, `UpdatedById` |
| `Remove` + commit | `DeletedAt`, `DeletedById` — and the row **survives** (soft delete) |
| `DeleteManyAsync(filter)` | a set-based *update* stamping the same members, server-side |

Reads exclude soft-deleted rows automatically; audit/restore screens read them deliberately:

```csharp
var everything = await repo.GetFilteredAsync(
    c => c.DeletedAt != null,
    new QueryOptions<Contract>().IgnoringQueryFilters());
```

## Testing it

`Clock` is a `TimeProvider` — freeze it and the whole lifecycle becomes deterministic:

```csharp
var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero));
services.AddSingleton(new DataConventions { Clock = clock, CurrentUserId = _ => userId });
// ... Add + commit
Assert.That(contract.CreatedAt, Is.EqualTo(clock.GetUtcNow().UtcDateTime));
Assert.That(contract.CreatedById, Is.EqualTo(userId));
```

## Beyond stamps — change history

Stamps answer *who touched it last*. For a full change log (what changed, old and new values),
pair the stamps with an explicit history entity written in the same commit — the
[lean write model](../concepts/write-model.md) makes that natural, since your command handler
already knows the change it is applying:

```csharp
await contracts.ModifyAsync(contract);
await history.AddAsync(ContractChange.From(before, contract, userId));
await unitOfWork.CommitAsync();          // one flush — atomic on relational stores
```
