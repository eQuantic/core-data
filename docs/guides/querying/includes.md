# Includes — loading navigations

`Include` loads related entities alongside a read — **without lazy loading, without JOIN
explosions**, on every store, each through its own mechanism.

```csharp
var orders = await repo.GetFilteredAsync(
    o => o.Customer == "ana",
    new QueryOptions<Order>()
        .Include(nameof(Order.Buyer))                       // reference
        .Include(x => x.Items)                              // collection (typed)
        .Include("Buyer.Address"));                         // dotted nested path
```

## Shaping an included collection

A screen rarely wants every related row: it wants the last three steps, newest first, without the
skipped ones. The typed overload takes the shape the way a LINQ query over the collection reads:

```csharp
var runs = await repo.GetAllAsync(new QueryOptions<Run>()
    .Where(r => r.RepositoryId == repositoryId)
    .Include(r => r.Steps, steps => steps
        .Where(s => s.Status != "skipped")
        .OrderByDescending(s => s.StartedAt)
        .Take(3)));
```

Each run's `Steps` holds exactly the entities the filter keeps, in that order, after the skip and up
to the limit — **per parent**, not across the read. `Where` twice means both; `OrderBy`/`ThenBy`
append sorts and take a [placement of nulls](paging.md#where-nulls-go); `Skip`/`Take` apply after
the order. A shape applies to a navigation of the read entity itself (the first segment of a path);
a dotted path below it loads in full. A shape on a reference is refused — a filter, an order or a
limit means a collection.

## How it executes

| Store | An include | A shaped include |
|---|---|---|
| Relational | one follow-up `IN` query per path segment (the "split query" strategy — no cartesian duplication), split into as many statements as the dialect's parameter limit needs when the parents' keys outnumber it (2,100 on SQL Server) | the filter joins the `IN` in the `WHERE`, the order is its `ORDER BY`; a skip or a limit ranks each parent's rows with `ROW_NUMBER() OVER (PARTITION BY fk ORDER BY …)`, so **only the rows each parent keeps leave the database**; a parent's rows are always ranked within one statement, and `Take(0)` loads nothing without a query |
| MongoDB | a server-side `$lookup` per path | a `$lookup` with a pipeline — `localField`/`foreignField` correlate it, and the shape's `$match`, `$sort`, `$skip` and `$limit` run inside it, per parent, on the server (MongoDB 5.0 or later) |
| Cosmos DB | **one second query** per path segment over the related container: the parents' keys resolved and read with `IN` | the filter joins that query's `WHERE`; the order, the skip and the limit apply per parent as the rows are stitched — **every row the filter keeps travels**, the ones past each parent's limit are dropped |
| Cassandra | **a second query** per path segment over the related table: `IN` over the parents' keys, a query per chunk of 100 keys, under the read's gates | as Cosmos DB; when the foreign key is the related table's partition key and the shape orders by its clustering columns as stored (or not at all), the limit pushes down as **`PER PARTITION LIMIT`** |

```sql
-- relational, the shaped include above:
SELECT … FROM (SELECT …, ROW_NUMBER() OVER (PARTITION BY "run_id" ORDER BY "started_at" DESC) AS "eq_rank"
               FROM "steps" WHERE "run_id" IN (@p0, @p1) AND ("status" <> @p2 OR "status" IS NULL)) "eq_ranked"
WHERE "eq_rank" > @p3 AND "eq_rank" <= @p4 ORDER BY "eq_rank"
```

Foreign keys resolve by convention — reference `Buyer` → `BuyerId` on the order; collection of
`OrderItem` → `OrderId` on the element. On the relational engine the fluent `Reference(…)`/
`Collection(…)` override it; on the document stores and Cassandra, `[ForeignKey("Order")]`
(`System.ComponentModel.DataAnnotations.Schema`) does. Dotted paths recurse segment by segment.

### What each store costs

- **Relational and MongoDB** join on the server: one more statement (or pipeline stage) per path,
  and the shape trims the related rows before they travel.
- **Cosmos DB** has no join across containers. The second query is cross-partition unless the
  related container is partitioned by the foreign key; its RU charge grows with the rows the filter
  keeps, not with the limit, because a per-parent limit has no Cosmos SQL form. Filter what you
  can, and keep the limit for display. An id is unique within a partition only, so when two
  documents of the referenced container hold the id a reference names, the include refuses
  (`InvalidOperationException`) rather than attach either of them, one of which may be another
  tenant's: give such documents ids unique across the container.
- **Cassandra** has no join either. The second query runs through the same
  [gates](pushdown.md) as any read, **under the read's own opt-ins**: a read on a foreign key that
  is not the related table's partition key names none of its partitions, so it is a scan and needs
  `.AllowFiltering()`; a filter CQL cannot express (`!=`, a null test) needs
  `.AllowClientEvaluation()`, and so does a foreign key outside the primary key, which takes no CQL
  `IN`: the table is read whole and the parents' keys matched client-side. Both opt-ins go on the
  parent's options. Model the related table partitioned by the foreign key and clustered in the order
  it is read, and the include is partition-routed queries with `PER PARTITION LIMIT`, 100 parents' keys
  each.

## Honest limits

- Includes compose with filtered/paged reads; they do not compose with streaming
  (`GetStreamAsync`) — refused with guidance — or with mapped and grouped reads (project after
  including; a [relation count](projections.md#counting-a-relation) counts without loading).
- A reference include targeting a **composite-keyed** entity refuses on the relational engine: a
  single FK column cannot address a two-column key. Load it explicitly with its key tuple.
- Including a member that is not a navigation (a Cassandra list column, say) is refused, naming the
  member: a row's own columns already load with it.
- On MongoDB a shaped include writes the joined array into the navigation's own element, so the
  navigation must stay class-mapped (leave it `null` when writing the parent).
- Include paths are strings (`nameof` keeps them safe) because they travel well — the wire-format
  options carry them unchanged; the typed overload records the same path.
