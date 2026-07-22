# Includes — loading navigations

`Include` loads related entities alongside a read — **without lazy loading, without JOIN
explosions, and only where the store genuinely has relations to load**.

```csharp
var orders = await repo.GetFilteredAsync(
    o => o.Customer == "ana",
    new QueryOptions<Order>()
        .Include(nameof(Order.Buyer))                       // reference
        .Include($"{nameof(Order.Items)}")                  // collection
        .Include("Buyer.Address"));                         // dotted nested path
```

## How it executes

**Relational**: each navigation loads with **one follow-up `IN` query** per path segment (the
"split query" strategy — no cartesian JOIN duplication):

```sql
SELECT ... FROM orders WHERE customer = @p0;
SELECT ... FROM buyers WHERE code IN (@k0, @k1, ...);       -- stitched onto Order.Buyer
SELECT ... FROM order_items WHERE order_code IN (...);      -- grouped onto Order.Items
```

Foreign keys resolve by convention — reference `Buyer` → `BuyerId` on the order; collection of
`OrderItem` → `OrderId` on the element — with fluent `Reference(…)`/`Collection(…)` overriding for
schemas the convention does not fit. Dotted paths recurse segment by segment.

**MongoDB**: includes execute as `$lookup` stages where declared. Documents that *embed* their
related data (the idiomatic MongoDB design) need no include at all — the engine does not force a
relational shape onto documents.

**Cassandra / Cosmos DB**: there are no server-side relations to load, and pretending otherwise
would hide N+1 fan-outs behind an innocent-looking option. `Include` **refuses** with the store's
reasoning:

> `Cassandra rows are self-contained; there are no navigations to include — model related data
> with the partition key or query it explicitly.`

## Honest limits

- Includes compose with filtered/paged reads; they do not compose with streaming
  (`GetStreamAsync`) — refused with guidance.
- A reference include targeting a **composite-keyed** entity refuses: a single FK column cannot
  address a two-column key. Load it explicitly with its key tuple.
- Include paths are strings (`nameof` keeps them safe) because they travel well — the wire-format
  options carry them unchanged.
