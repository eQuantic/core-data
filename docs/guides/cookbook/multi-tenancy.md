# Cookbook — multi-tenancy

Row-level multi-tenancy with **global query filters**: every read scoped to the current tenant,
declared once, impossible to forget at a call site.

## The recipe

```csharp
// 1. A per-request tenant accessor (however your auth works):
public interface ITenantAccessor { Guid? TenantId { get; } }

// 2. The filter, registered once — the factory runs per scope, so it reads the current request:
services.AddSingleton(new QueryFilters()
    .For<Order>(scope =>
        scope.GetService(typeof(ITenantAccessor)) is ITenantAccessor { TenantId: { } tenant }
            ? order => order.TenantId == tenant
            : null)                                     // no tenant in scope → no filter (e.g. jobs)
    .For<Invoice>(scope =>
        scope.GetService(typeof(ITenantAccessor)) is ITenantAccessor { TenantId: { } tenant }
            ? invoice => invoice.TenantId == tenant
            : null));
```

Every read on `Order`/`Invoice` — filtered, paged, aggregated, grouped, unioned — now carries
`tenantId == current` ANDed into whatever else the query says. The filter goes through the same
[pushdown pipeline](../querying/pushdown.md) as any predicate, so it executes server-side.

## Escaping deliberately

```csharp
// admin/reporting paths opt out per query — visible in the code, greppable in review:
var all = await repo.GetAllAsync(new QueryOptions<Order>().IgnoringQueryFilters());
```

`IgnoringQueryFilters()` also skips the soft-delete live-rows filter — it means *all rows*, and
that is the point of making it explicit.

## Store-specific leverage

- **Cosmos DB**: make the tenant the **partition key** (`[PartitionKey]` on `TenantId`) — the
  global filter then *pins the partition* on every read: isolation and the cheapest RU path from
  the same declaration. With [hierarchical keys](../providers/cosmosdb.md), `Tenant` + `Region`
  gives both isolation and locality.
- **Cassandra**: same idea — the tenant leads the partition key, so tenant-scoped reads are
  partition-routed by construction.
- **Relational**: add the tenant column to the [clustering declaration](../modeling/annotations.md)
  or leading index positions so tenant-scoped scans stay index-driven.

## Writes

Global filters scope *reads*. For writes, stamp the tenant at creation (an `ITenantAccessor`-aware
factory, or a `CreatedById`-style convention of your own) and let
[optimistic concurrency](../writing/concurrency.md) protect cross-tenant races the same way it
protects everything else.
