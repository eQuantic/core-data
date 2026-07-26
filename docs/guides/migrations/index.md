# Migrations

Typed, fluent, model-driven schema — on **all six stores**, with one authoring surface. No SQL
strings for the common cases, member selectors instead of field-name strings, and a `Run(...)`
escape hatch with direct provider access when the fluent surface is not enough.

## Anatomy

```csharp
using eQuantic.Core.Data.Migration;

[Migration("Products setup", 2026, 7, 22, 12, 0, 0)]   // name + timestamp = identity and order
public sealed class ProductsSetup : Migration
{
    public override void Up(IMigrationBuilder migration) => migration
        .For<Product>(product => product
            .EnsureCollection()                          // table/collection/container from the model
            .Index(x => x.Category)
            .Index(x => x.Name, o => o.Unique().Named("ux_products_name"))
            .Index(x => x.Price, o => o.Filtered(x => x.Quantity > 0))     // partial index, typed predicate
            .Index(x => x.Attributes, o => o.Gin())                        // PostgreSQL jsonb GIN
            .Index(x => x.Description, o => o.Text())                      // MongoDB text index
            .Index(x => x.SeenAt, o => o.Ttl(TimeSpan.FromDays(7)))        // MongoDB TTL index
            .CompositeIndex(keys => keys.Descending(x => x.Price).Ascending(x => x.Name)))
        .For<LegacyNote>(note => note
            .AddField(x => x.Stars)                      // evolve a live schema
            .RenameField(x => x.Text, "body")
            .ConvertField(x => x.Stars, MigrationFieldType.String, MigrationFieldType.Int32)
            .DropField("legacy_score")
            .Update(x => x.Stars < 0, update => update.Set(x => x.Stars, 0)))   // typed data migration
        .Run(async (context, ct) =>
        {
            // direct provider access when needed:
            // context.AsRelational()... / context.AsMongo()... / context.AsCassandra().Session ...
        });
}
```

## Running

```csharp
services.AddPostgreSqlMigrations(typeof(ProductsSetup).Assembly);   // discover by scanning
// …or name them explicitly (no reflection — required for trimming/NativeAOT, and a faster startup):
services.AddPostgreSqlMigrations(source => source.Add<ProductsSetup>().Add<ProductsBackfill>());

var applied = await provider.GetRequiredService<IMigrationRunner>().RunAsync();
```

Both forms exist on every provider and behave identically: the `[Migration]` timestamp orders the
run, so registration order never matters. Two different migrations claiming the same id is a
mistake and throws — see [Trimming and NativeAOT](../operations/aot.md) for why the explicit form
is the AOT-safe one.

The runner discovers migrations in the given assemblies, orders them by timestamp, applies each
**once** and records it in the history (a `_migrations` table/collection/container per store).
Re-running is a no-op; multiple services racing on startup are guarded by the history write.

## `EnsureCollection()` — the model is the schema

The single most used operation derives schema from the model, so schema and mapping cannot drift:

| Store | `EnsureCollection()` creates |
|---|---|
| Relational | `CREATE TABLE IF NOT EXISTS` with typed columns (facets applied: `varchar(n)`, `numeric(p,s)`), single or **composite** `PRIMARY KEY`, identity DDL — plus the model's search indexes (PostgreSQL GIN trigram) and clustering index |
| MongoDB | the collection, the model's TTL index and clustering compound index |
| Cosmos DB | the container with partition key path(s) — **hierarchical multi-hash included** — default TTL and the model's composite indexes |
| Cassandra | `CREATE TABLE` with partition/clustering keys, `CLUSTERING ORDER BY`, `default_time_to_live`, plus the model's SASI search indexes |

## Store honesty in migrations

Operations that a store cannot express refuse with guidance rather than approximating:

- Cassandra cannot change a column's type → `ConvertField` points you to add-and-backfill via `Run`.
- Cosmos has no set-based unset → `DropField` explains the per-document rewrite path.
- MySQL has no filtered indexes → `Filtered(...)` says so.
- Cassandra secondary indexes are single-column → `CompositeIndex` points to the primary key or `Run`.

The same message discipline as queries: what, why, and the way out.

## Design decisions, stated

- **Forward-only.** There is no `Down()`. Rollbacks in production are new forward migrations —
  the model most operations teams actually run. This is a deliberate simplification, not an
  omission.
- **Timestamps, not sequence numbers**, so parallel branches merge without renumbering.
- **The escape hatch is first-class.** `Run(...)` receives the provider's execution context and a
  cancellation token — anything the store can do, a migration can do, without leaving the
  ordered/recorded pipeline.

## Writing them for you

Everything above is the authoring surface, and it stays the surface: the `eqdata` tool compares your
model against a snapshot committed beside it and writes one of these classes, which you then read and
edit. It also checks a live database for having stopped matching the model — the one question the
migration history cannot answer. See [Generating and drift checking](tooling.md).
