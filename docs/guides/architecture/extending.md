# Extending — dialects and the SPI

## Writing a relational dialect

The cheapest way to support another SQL engine is a `SqlDialect` subclass — the shared engine does
the rest. The dialect answers only the questions engines disagree on:

```csharp
public sealed class DuckDbDialect : SqlDialect
{
    public override string Quote(string identifier) => $"\"{identifier}\"";
    public override string ColumnName(string memberName) => ToSnakeCase(memberName);
    public override string TableName(string typeName) => ToSnakeCase(typeName);
    public override string SqlType(Type type) => /* CLR → DDL type map */;
    // paging syntax, generated-key DDL + retrieval, parameter configuration,
    // index structures (CreateIndexSql / SearchIndexSql), sized types (SizedTextType)…
    // every member has a base implementation or a documented reason to override.
}
```

Register it the way the shipped providers do (their `Extensions/` folders are the template): a
`*Database` extension building the `RelationalModelBuilder` with your dialect, plus the shared
`AddRelationalRepositories`/`AddRelationalMigrations` wiring. The MariaDB dialect inside the MySql
package is a complete worked example of "same engine, different truths" — it overrides generated-key
retrieval and nothing else.

**The dialect contract is honesty**: for a structure your engine lacks (filtered indexes, GIN),
throw `NotSupportedException` with guidance — the base class shows the message style. Never emit
approximate DDL.

## Custom unit of work

Every provider's `Add*Repositories<TUnitOfWork>()` overload accepts your subclass of the provider's
unit of work — the seam for cross-cutting write behavior (outbox staging, domain-event dispatch on
commit) without giving up the engine:

```csharp
public class ShopUnitOfWork(IServiceProvider sp, /* provider deps */) : PostgreSqlUnitOfWork(sp, …)
{
    // override commit hooks, expose typed repositories, etc.
}
services.AddPostgreSqlRepositories<ShopUnitOfWork>();
```

## Custom repositories

Subclass the provider repository to add domain query methods while inheriting the whole engine
surface:

```csharp
public sealed class OrderRepository(IQueryableUnitOfWork uow)
    : RelationalRepository<Order, Guid>(uow), IOrderRepository
{
    public Task<IEnumerable<Order>> OverdueAsync(DateTime today) =>
        GetFilteredAsync(o => o.DueDate < today && o.Status == OrderStatus.Open);
}
```

## The SPI boundary

`eQuantic.Core.Data.Query` (the interpreters and IR) is public **for providers**, hidden from
IntelliSense (`[EditorBrowsable(Never)]`), and versioned as SPI: if you build a full provider on
it, pin minor versions and read release notes — the repository contracts are the stable API,
the IR is the extensible engine seam. A new provider implements: a model builder with the
annotation pre-pass (the pattern is identical in all four shipped ones), renderers from the IR to
the store's query/write forms, a unit of work with staged writes, and the repository classes over
it. The Cassandra provider is the best reference implementation — it exercises every honesty
mechanism the engine has.
