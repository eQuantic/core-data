# UNION and UNION ALL

The feature ORMs always forget: combining differently-typed entities into one shape, server-side.
Activity feeds, unified search, "everything for this customer" screens.

```csharp
using eQuantic.Core.Data.Query;

var query = UnionQuery.All(                     // UNION ALL — or UnionQuery.Distinct(...) for UNION
    Union.Of<Order>()
        .Where(o => o.Customer == customerId)
        .Select(o => new ActivityRow(o.Id, "order", o.PlacedAt)),
    Union.Of<Ticket>()
        .Where(t => t.CustomerId == customerId)
        .Select(t => new ActivityRow(t.Id, "ticket", t.OpenedAt)))
    .OrderByDescending(row => row.At)
    .Take(50);

var runner = (IUnionQueryRunner)unitOfWork;
IReadOnlyList<ActivityRow> feed = await runner.UnionAsync(query, cancellationToken);
```

Each branch carries its own filters (each entity's global filters included, unless the branch opts
out) and its own projection into the **common result shape**; ordering and paging apply to the
combined rows.

## Execution per store

| Store | How it runs |
|---|---|
| Relational | one statement: `SELECT … UNION [ALL] SELECT … ORDER BY … LIMIT …` — the database does everything |
| MongoDB | **one aggregation**: the first branch's pipeline plus a `$unionWith` per additional branch; `Distinct` deduplicates with a `$group` over the combined shape |
| Cosmos DB / Cassandra | no native cross-container/table union exists — refused with guidance (run the branches and combine client-side, consciously) |

`Distinct` compares the full projected shape — the same semantics SQL `UNION` has. `Skip` without
`Take` is refused (unbounded skips over merged sets are a footgun; the message says to add `Take`).

## Design notes

- The projections must produce the **same shape** (same member names/types); the engine validates
  and names the mismatch otherwise.
- `OrderBy` selects a *projected* member — you order the combined rows, not either source.
- Branch count is unbounded; each branch is a full citizen (filters, specifications, query filters).
