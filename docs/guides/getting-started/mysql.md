# Getting started — MySQL / MariaDB

The MySQL provider implements the eQuantic.Core.Data contracts directly on
[MySqlConnector](https://mysqlconnector.net/). MariaDB gets its **own dialect** — same package,
different registration — because the engines genuinely differ (MariaDB supports
`INSERT … RETURNING`; MySQL does not, and the dialect honestly reflects that).

## 1. Install

```bash
dotnet add package eQuantic.Core.Data.MySql
```

## 2. Define an entity

```csharp
using eQuantic.Core.Data.Repository;

public sealed class Product : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Price { get; set; }

    public Guid GetKey() => Id;
    public void SetKey(Guid key) => Id = key;
}
```

## 3. Register

```csharp
// MySQL:
services.AddMySqlDatabase(connectionString, model => model
    .Entity<Product>(_ => { }));
services.AddMySqlRepositories();
services.AddMySqlMigrations(typeof(Program).Assembly);

// MariaDB — same contracts, MariaDB dialect (generated keys via INSERT … RETURNING):
services.AddMariaDbDatabase(connectionString, model => model
    .Entity<Product>(_ => { }));
services.AddMySqlRepositories();
```

## 4. Schema and usage

Identical to every other provider — that is the point:

```csharp
[Migration("Products setup", 2026, 7, 22, 12, 0, 0)]
public sealed class ProductsSetup : Migration
{
    public override void Up(IMigrationBuilder migration) => migration
        .For<Product>(product => product.EnsureCollection().Index(x => x.Category));
}
```

```csharp
await repository.AddAsync(new Product { Name = "Keyboard", Category = "Peripherals", Price = 49.90m });
await unitOfWork.CommitAsync();

var found = await repository.GetFilteredAsync(p => p.Category == "Peripherals");
```

## MySQL vs MariaDB — what the dialects change

| Capability | MySQL | MariaDB |
|---|---|---|
| Generated-key readback | `LAST_INSERT_ID` semantics | `INSERT … RETURNING` |
| Filtered (partial) indexes | Rejected with guidance (the engine has none) | Rejected with guidance |
| Everything else | identical | identical |

## Where next

- [MySQL / MariaDB deep dive](../providers/mysql.md)
- [Modeling](../modeling/overview.md) · [Querying](../querying/pushdown.md) · [Migrations](../migrations/index.md)
