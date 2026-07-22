# Getting started — SQL Server

The SQL Server provider implements the eQuantic.Core.Data contracts directly on
[Microsoft.Data.SqlClient](https://learn.microsoft.com/sql/connect/ado-net/microsoft-ado-net-sql-server).

## 1. Install

```bash
dotnet add package eQuantic.Core.Data.SqlServer
```

## 2. Define an entity

```csharp
using eQuantic.Core.Data.Repository;

public sealed class Ticket : IEntity<long>
{
    public long Id { get; set; }              // database-generated (identity)
    public string Label { get; set; } = "";

    public long GetKey() => Id;
    public void SetKey(long key) => Id = key;
}
```

## 3. Register

```csharp
services.AddSqlServerDatabase(connectionString, model => model
    .Entity<Ticket>(entity => entity.Key(x => x.Id, generated: true)));
services.AddSqlServerRepositories();
services.AddSqlServerMigrations(typeof(Program).Assembly);
```

`generated: true` declares an identity key: inserts omit the column and the commit **reads the
generated value back** (`OUTPUT INSERTED` on SQL Server) and assigns it to the entity — after
`CommitAsync()`, `ticket.Id` carries the database's value.

## 4. Schema and usage

```csharp
[Migration("Tickets setup", 2026, 7, 22, 12, 0, 0)]
public sealed class TicketsSetup : Migration
{
    public override void Up(IMigrationBuilder migration) => migration
        .For<Ticket>(ticket => ticket.EnsureCollection());
}
```

```csharp
var ticket = new Ticket { Label = "first" };
await repository.AddAsync(ticket);
await unitOfWork.CommitAsync();
Console.WriteLine(ticket.Id);   // the identity value, read back by the flush
```

SQL Server specifics worth knowing early:

- Strings default to `nvarchar(450)` (indexable); size them explicitly with
  `[Facet(Length = …)]` — see [Modeling](../modeling/annotations.md).
- Filtered indexes are fully supported (`.Index(x => x.Price, o => o.Filtered(x => x.Quantity > 0))`).

## Where next

- [SQL Server deep dive](../providers/sqlserver.md)
- [Modeling](../modeling/overview.md) · [Querying](../querying/pushdown.md) · [Migrations](../migrations/index.md)
