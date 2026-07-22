# Conventions and `DataConventions`

Conventions are the zero-declaration layer: what the engine assumes when neither annotations nor
fluent configuration say otherwise — plus a small, explicit settings object (`DataConventions`)
for the behaviors that are policy, not mapping.

## Naming and shape conventions

| Concern | Convention |
|---|---|
| Key | the member named `Id` (`[EntityKey]` / `Key(…)` override) |
| Table / collection / container name | the entity type name, through the store's naming style |
| Column names (relational) | the dialect's convention — snake_case on PostgreSQL (`CreatedAt` → `created_at`) |
| Element names (MongoDB) | the member name as-is |
| Element names (Cosmos DB) | camelCase (System.Text.Json web defaults) |
| Columns vs navigations (relational) | scalars (and scalar collections, for stores with array columns) map to columns; entity references and entity collections are navigations |
| Foreign keys (relational `Include`) | reference `Order` → `OrderId` on this entity; collection of `Item` → `{Entity}Id` on the element (`Reference(…)`/`Collection(…)` override) |

## `DataConventions` — the write-policy object

Registered in DI (or defaulted), read by every provider's unit of work:

```csharp
services.AddSingleton(new DataConventions
{
    Clock = TimeProvider.System,          // the time source for every stamp (fake it in tests)
    LifecycleStamps = true,               // CreatedAt/UpdatedAt stamping on/off
    SoftDelete = true,                    // soft-delete conversion on/off
    CurrentUserId = services =>           // WHO stamps: resolved per request
        services.GetService(typeof(ICurrentUser)) is ICurrentUser user ? user.Id : null,
});
```

## Lifecycle interfaces — time

Entities opt into lifecycle behavior by implementing the `eQuantic.Core.Domain` time interfaces
(directly, or via the `eQuantic.Core.DataModel` base classes, which already do):

| Interface | Member | What the engine does |
|---|---|---|
| `IEntityTimeMark` | `CreatedAt` | stamped once, on first persist |
| `IEntityTimeTrack` | `UpdatedAt?` | stamped on every write |
| `IEntityTimeEnded` | `DeletedAt?` | `Remove` becomes a **soft delete** (stamp + keep the row), and every read gets an automatic live-rows filter (`DeletedAt == null`) — opt out per query with `.IgnoringQueryFilters()` |

## WHO stamps — by property name

Attribution needs no interface: if the entity has a `CreatedById` / `UpdatedById` / `DeletedById`
property and `CurrentUserId` is configured, the engine stamps it at the corresponding moment. The
`eQuantic.Core.DataModel` who-interfaces (`IEntityOwned`, `IEntityTrack`, `IEntityHistory`) give
those properties their canonical shape, but any property with the conventional name participates.

```csharp
public sealed class Document : IEntity<Guid>, IEntityTimeMark, IEntityTimeTrack, IEntityTimeEnded
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

With that entity: `Add` stamps `CreatedAt` + `CreatedById`; every write stamps `UpdatedAt` +
`UpdatedById`; `Remove` stamps `DeletedAt` + `DeletedById` and keeps the row; reads exclude deleted
rows unless asked not to; and set-based `DeleteManyAsync` on a soft-delete entity becomes a
set-based *update* stamping the same members. All of it uniform across every provider, all of it
driven by `DataConventions.Clock` — so tests can freeze time.

See [Lifecycle — stamps and soft delete](../writing/lifecycle.md) for the write-path details, and
[Auditing](../cookbook/auditing.md) for the end-to-end recipe.
