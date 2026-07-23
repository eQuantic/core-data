# Bulk loading and raw SQL

Two escape hatches for the moments the ordinary write model and the pushdown engine are the wrong
tool: loading a lot of rows at once, and running SQL the engine cannot express.

## Bulk loading

`BulkInsertAsync` streams entities through the store's **native bulk mechanism**, bypassing the
staged flush entirely — no statement per row:

```csharp
var uow = scope.ServiceProvider.GetRequiredService<PostgreSqlDefaultUnitOfWork>();

await uow.BulkInsertAsync(rows);        // rows: IEnumerable<Product>
```

| Store | Mechanism |
|---|---|
| PostgreSQL | binary `COPY … FROM STDIN` — the fastest load path the server offers |
| SQL Server | `SqlBulkCopy` |
| MySQL / MariaDB | `MySqlBulkCopy` (see the requirement below) |
| Cassandra, MongoDB, Cosmos DB | no native equivalent — the ordinary commit already batches natively |

It runs **immediately** (like the other set-based writes), joins an open explicit transaction when
there is one — so a rollback takes the loaded rows with it — and applies the
[lifecycle stamps](lifecycle.md) exactly as a staged insert would.

### The honest limits

- **Generated keys are not read back.** Bulk paths do not return them; assign client-side keys
  (`Guid`) for entities you bulk-load. A `[EntityKey(Generated = true)]` column is simply excluded
  from the load and left to the database.
- **Concurrency tokens are stamped, not checked** — there is nothing to conflict with on an insert.
- **A dialect without a native path refuses.** It does not quietly fall back to an ordinary batch:
  "bulk" that is secretly row-by-row is precisely the hidden cost this engine does not ship. The
  message points you to staging and `Commit()`, which already batches into one round trip.
- **MySQL needs `LOAD DATA LOCAL INFILE` on both sides**: `AllowLoadLocalInfile=true` in the
  connection string *and* `local_infile=1` on the server. That is a deployment decision with
  security weight (the server can request local files from the client), so the engine surfaces the
  requirement instead of enabling it for you — with a message that names both settings.

## Raw SQL, typed

`QueryAsync<TResult>` runs arbitrary SQL and materializes each row **by column name** — the
Dapper-style escape hatch for window functions, CTEs, vendor extensions, or a reporting shape that
is nobody's entity:

```csharp
public sealed class CategoryTotal
{
    public string Category { get; set; } = "";
    public long Orders { get; set; }
    public decimal Total { get; set; }
}

var totals = await uow.QueryAsync<CategoryTotal>(
    """
    SELECT category, COUNT(*) AS orders, SUM(total) AS total
    FROM sale_orders WHERE placed_at >= @p0 GROUP BY category
    """,
    [since]);
```

Matching is case-insensitive and snake_case-tolerant (`created_at` binds `CreatedAt`); values
convert through the same coercions entity materialization uses; result columns with no matching
member are ignored and members with no matching column keep their default. The ordinal-to-member
plan is computed once per result shape and cached.

For non-queries there is `ExecuteAsync`, which reports the rows affected:

```csharp
var affected = await uow.ExecuteAsync("UPDATE sale_orders SET status = @p0 WHERE status = @p1",
    ["archived", "closed"]);
```

To materialize **the entity itself** from custom SQL, the read repository already has
`FromSqlAsync(sql, parameters)`.

### What you give up — deliberately

The SQL is yours, so nothing the engine normally guarantees applies to it: **no global query
filter, no soft-delete filter, no pushdown analysis, no `Explain()`**. Parameters bind positionally
as `@p0, @p1…` — never interpolate values into the text. That is the trade the escape hatch exists
to make, and it is the reason the rest of the surface stays typed.
