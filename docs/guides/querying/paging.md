# Sorting and paging

Three paging models, because there are three honest ways to page — and stores differ in which they
can serve well. A fourth shape, a page without its total, is a plain read with `Skip`/`Take`.

## Sorting

```csharp
new QueryOptions<Product>()
    .OrderBy(p => p.Price)
    .ThenByDescending(p => p.Name)
// or by path (wire-friendly): .OrderBy("price") / .OrderBy("price:asc,name:desc")
```

Sorting pushes down everywhere, with each store's truth applied:

- **Relational, MongoDB, Cosmos DB** — arbitrary members sort server-side.
- **Cassandra** — only clustering keys can `ORDER BY` (that is the storage engine's rule, not the
  provider's); anything else throws with the reason, unless the read opts into
  `.AllowClientEvaluation()`: the fetched rows are then sorted client-side and any limit applies
  after the sort (a read not scoped to a partition needs `.AllowFiltering()` as well, since the
  fetch scans the table). Model the read order with `[ClusteringKey(Descending = …)]` and it stays
  on the cluster.

### Where nulls go

Stores disagree on where a null sorts: PostgreSQL puts nulls **last** ascending, SQL Server,
MySQL, MariaDB, MongoDB and Cosmos DB put them **first**. A read that depends on the placement says
so, and every store returns that order:

```csharp
new QueryOptions<Card>().OrderBy(c => c.Position, NullsOrder.First)        // null, 1, 2
new QueryOptions<Card>().OrderByDescending(c => c.Position, NullsOrder.Last) // 2, 1, null
```

| Store | How the placement is honoured | Cost |
|---|---|---|
| PostgreSQL | native `NULLS FIRST` / `NULLS LAST` | none |
| SQL Server, MySQL, MariaDB | the order of an is-null expression first: `CASE WHEN col IS NULL THEN 0 ELSE 1 END, col` | the expression defeats an index on the column for that sort |
| MongoDB | nulls and missing fields sort as the smallest value, so `First` ascending and `Last` descending are native; the other two sort on an is-null flag (`$addFields` with `$type`, `$sort`, then the flag removed) | the flag is computed per document before the sort |
| Cosmos DB | nulls and undefined sort as the smallest value, so `First` ascending and `Last` descending are native; `ORDER BY` takes property paths only, so the other two read as **two queries** — the rows whose key is null and the rest — concatenated in the order asked, the skip and the limit carried across them | a second query, and a count of the first part when a skip passes it whole; refused on a later sort (`ThenBy`) and on continuation paging or streaming, which cannot split |
| Cassandra | a clustering key is never null, so the placement is already honoured on one; any other member sorts client-side behind `.AllowClientEvaluation()`, nulls placed as asked | the read fetches every matching row before the limit |

`NullsOrder.Default` keeps the store's own placement — the order every read had before.

## A page without its total

`Skip`/`Take` on the options shape what a plain read returns, and **no count runs** — the page a
screen needs when it does not show "page 3 of 40":

```csharp
var page = await repo.GetAllAsync(new QueryOptions<Card>()
    .Where(c => c.Board == board)
    .OrderBy(c => c.Rank)
    .Skip(10).Take(10));                 // rows 11 to 20 of the order
```

They apply to the reads that return rows — `GetAll`, `GetFiltered`, `AllMatching`, `GetMapped`,
`GetFirst`/`GetSingle` (within their own limit of one or two rows) and `Get`. Reads that work over
every matching row (`Count`, `Any`, `All`, the sums and aggregates, `GroupBy`) and the reads that
page by themselves (`GetPaged`, `GetPage`, `GetStream`) **refuse** them rather than count a page or
page twice. A skip without an order orders by the key (the id on the document stores; Cassandra
keeps its own token and clustering order), so pages are stable. A skip without a take returns every
row after it, and `Take(0)` returns no row without asking the store.

| Store | Executes as |
|---|---|
| Relational | the dialect's `LIMIT … OFFSET …` (`OFFSET … FETCH` on SQL Server); a skip alone as `OFFSET n` on PostgreSQL, `OFFSET n ROWS` on SQL Server, and `LIMIT 18446744073709551615 OFFSET n` on MySQL and MariaDB, which take an offset only after a limit |
| MongoDB | `$skip` + `$limit`, before any `$lookup` include |
| Cosmos DB | `OFFSET … LIMIT …` |
| Cassandra | no `OFFSET` exists: the first `skip + take` rows are fetched (`LIMIT`) and the skipped ones dropped |

## 1. Offset paging — `GetPagedAsync`

The classic page-number UI. Two queries: a `COUNT` and a page.

```csharp
PagedResult<Product> page = await repo.GetPagedAsync(
    new PageRequest(pageIndex: 3, pageSize: 20),
    new QueryOptions<Product>().OrderBy(p => p.Name));
// page.Items, page.TotalCount, page.PageIndex, page.PageSize
```

Relational stores use `OFFSET/LIMIT`. Cassandra has no OFFSET — the provider honestly fetches the
first `skip + take` rows and slices (fine for shallow pages; for deep scans use token paging).
When no sorting is given, relational pages **order by the key** so pages are stable.

## 2. Continuation-token paging — `GetPageAsync`

The infinite-scroll / API-cursor model: no total count, an opaque token per page, O(1) deep pages.

```csharp
ContinuedResult<Product> first = await repo.GetPageAsync(pageSize: 50);
ContinuedResult<Product> next  = await repo.GetPageAsync(50, first.ContinuationToken);
// until ContinuationToken == null
```

Each store pages its native way — the token is opaque on purpose:

| Store | Mechanism |
|---|---|
| Relational | **keyset** over the key column (`key > last ORDER BY key LIMIT n`) — deep pages cost an index seek, not a scan |
| Cosmos DB | the SDK's native continuation token |
| Cassandra | the driver's paging state |
| MongoDB | keyset over `_id` |

Two honest limits, enforced with clear messages: custom sortings do not compose with keyset paging
(the token orders by the key), and relational **composite keys** refuse token paging (use offset
paging) until keyset-over-tuples ships.

## 3. Streaming — `GetStreamAsync`

For batch jobs over large sets: an `IAsyncEnumerable<TEntity>` that holds one page in memory at a
time and stops fetching when you stop iterating.

```csharp
await foreach (var product in repo.GetStreamAsync(options, cancellationToken))
{
    await Process(product);
}
```

## Choosing

| Need | Use |
|---|---|
| A page, no total shown ("load more", a dashboard's top ten) | `Skip`/`Take` on any read |
| Page-number UI, total count shown | `GetPagedAsync` |
| API cursors, infinite scroll, deep pagination | `GetPageAsync` |
| Process everything, bounded memory | `GetStreamAsync` |
