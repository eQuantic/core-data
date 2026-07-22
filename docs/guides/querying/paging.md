# Sorting and paging

Three paging models, because there are three honest ways to page — and stores differ in which they
can serve well.

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
  provider's); anything else throws with the reason. Model the read order with
  `[ClusteringKey(Descending = …)]`.

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
| Page-number UI, total count shown | `GetPagedAsync` |
| API cursors, infinite scroll, deep pagination | `GetPageAsync` |
| Process everything, bounded memory | `GetStreamAsync` |
