# Getting started — PostgreSQL

The PostgreSQL provider implements the eQuantic.Core.Data contracts directly on
[Npgsql](https://www.npgsql.org/) — no Entity Framework anywhere in the stack.

## 1. Install

```bash
dotnet add package eQuantic.Core.Data.PostgreSql
```

## 2. Define an entity

An entity is a plain class implementing `IEntity<TKey>`. No base class is required, and no driver
attributes ever appear on it:

```csharp
using eQuantic.Core.Data.Repository;

public sealed class Product : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Price { get; set; }
    public int Quantity { get; set; }

    public Guid GetKey() => Id;
    public void SetKey(Guid key) => Id = key;
}
```

By convention, the member named `Id` is the key and column names go through the dialect's naming
convention (`Name` → `name`, `CreatedAt` → `created_at` on PostgreSQL). Everything is overridable —
see [Modeling](../modeling/overview.md).

## 3. Register

```csharp
services.AddPostgreSqlDatabase(connectionString, model => model
    .Entity<Product>(_ => { }));          // conventions cover this entity entirely
services.AddPostgreSqlRepositories();

// optional: typed, fluent migrations (see the Migrations guide)
services.AddPostgreSqlMigrations(typeof(Program).Assembly);
```

`AddPostgreSqlRepositories()` registers the open generics — `IRepository<,>`,
`IAsyncRepository<,>`, `IQueryableRepository<,>`, `IAsyncQueryableRepository<,>` — over a scoped
unit of work, so any entity registered in the model resolves a repository with **zero classes
written by you**.

## 4. Create the schema

Schema lives in migrations — typed, fluent, derived from the model:

```csharp
[Migration("Products setup", 2026, 7, 22, 12, 0, 0)]
public sealed class ProductsSetup : Migration
{
    public override void Up(IMigrationBuilder migration) => migration
        .For<Product>(product => product
            .EnsureCollection()            // CREATE TABLE from the model
            .Index(x => x.Category));
}
```

```csharp
// on startup:
await scope.ServiceProvider.GetRequiredService<IMigrationRunner>().RunAsync();
```

## 5. Read and write

```csharp
var repository = scope.ServiceProvider.GetRequiredService<IAsyncRepository<Product, Guid>>();
var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

// writes stage on the unit of work; one atomic batched flush runs on Commit
await repository.AddAsync(new Product { Name = "Keyboard", Category = "Peripherals", Price = 49.90m });
await repository.AddAsync(new Product { Name = "Mouse", Category = "Peripherals", Price = 19.90m });
await unitOfWork.CommitAsync();           // one transaction, generated keys read back

// reads
var peripherals = await repository.GetFilteredAsync(
    p => p.Category == "Peripherals" && p.Price < 30m,
    new QueryOptions<Product>().OrderBy(p => p.Price));

var page = await repository.GetPagedAsync(new PageRequest(pageIndex: 1, pageSize: 20),
    new QueryOptions<Product>().OrderBy(p => p.Name));

// set-based writes run immediately (no entity loading)
await repository.UpdateManyAsync(
    p => p.Category == "Peripherals",
    p => new Product { Quantity = p.Quantity + 10 });
```

On PostgreSQL the commit is **atomic**: one batched flush inside a transaction, with generated keys
read back through `RETURNING`.

## Where next

- [The contracts](../concepts/contracts.md) — what each interface is for.
- [Modeling](../modeling/overview.md) — annotations, fluent, conventions, `[Facet]`, composite keys.
- [PostgreSQL deep dive](../providers/postgresql.md) — jsonb, GIN/trigram indexes, filtered indexes,
  `RETURNING`, resilience.
