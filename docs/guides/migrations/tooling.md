# Generating migrations, and checking for drift

`eqdata` writes migrations for you by comparing the model against a snapshot committed beside it, and
tells you when a database has stopped matching the model. It generates ordinary source you read and
edit — it does not run anything, and it does not know what your data means.

```bash
dotnet tool install --global eQuantic.Core.Data.Tools
```

## The seam

The tool reads the model from your application, not from a second description of it. Implement
`IDesignTimeServices` once, anywhere in the project you point it at:

```csharp
using eQuantic.Core.Data.Evolution;

public sealed class DesignTimeServices : IDesignTimeServices
{
    public IServiceProvider Create(string[] args)
    {
        var services = new ServiceCollection();
        services.AddPostgreSqlDatabase(
            Environment.GetEnvironmentVariable("DB") ?? "Host=localhost;Database=shop;…",
            model => model.Entity<OrderData>(order => order.Table("orders").Key(x => x.Id)));
        return services.BuildServiceProvider();
    }
}
```

It hands back the whole provider rather than the model alone, because `drift` needs the same
connection the application uses, configured the same way.

> The project has to be an **executable**. A class library produces no `runtimeconfig.json`, and
> without one the tool cannot locate the NuGet assemblies your model depends on. Point `--project`
> at the application and keep the model wherever you like.

## `eqdata migrations add`

```bash
eqdata migrations add AddCustomerTier --project src/Shop.Api
```

Two files land in `Migrations/`: the migration, and a regenerated `DataModelSnapshot.g.cs` recording
the model as it now stands. **Commit them together.** A snapshot that moved past a change nobody
generated is worse than no snapshot — the next comparison starts from a state the database was never
brought to, and the missing change is never mentioned again.

Registration stays explicit, as it is for hand-written migrations:

```csharp
services.AddPostgreSqlMigrations(source => source.Add<AddCustomerTier>());
```

### Where it stops and asks

A generated file is a starting point. Where the tooling knows what moved but only you know what it
means, it emits a `#error` — **the solution does not build until you answer**. A comment would let
the change run, appear to succeed, and quietly leave the data wrong.

```csharp
migration.For<global::Shop.OrderData>(entity => entity
    .AddField(x => x.Tier)
    .Update(_ => true, set => set.Set(x => x.Tier, default!)));
#error 'Shop.OrderData.Tier' is added without saying what the records that already exist hold. …
```

Three things trigger it:

| | |
|---|---|
| A member added with no value declared | every existing record would take `default(T)` — a value nobody chose |
| A member that appeared while another disappeared, undeclared | generating drop-and-add loses the data (see below) |
| A change no store operation performs | resizing a column, renaming or dropping a collection |

### Renames keep the data — if you say so

A rename and a drop-and-add look identical in a diff, and only one of them keeps the values. The
comparison finds a rename by itself when the member kept its name and only its storage moved. When
the **member** was renamed, say where it came from:

```csharp
public sealed class OrderData
{
    [PreviousName("customer")]
    public string Buyer { get; set; } = "";
}
```

```csharp
model.Entity<OrderData>(order => order.PreviousName(x => x.Buyer, "customer"));
```

Either one turns a drop-and-add into `RenameField("customer", "buyer")`. Without it you get the pair,
flagged, with the fix named in the error.

### Declaring the value up front

Saying what existing records hold in the model removes the `#error` entirely:

```csharp
[DefaultValue("web")]
public string Channel { get; set; } = "";
```

```csharp
model.Entity<OrderData>(order => order.Default(x => x.Channel, "web"));
```

### Refusals

Some changes are not generated at all, and nothing is written when one appears — generating the rest
would move the snapshot past a change that never ran. Cassandra refuses a moved partition or
clustering key, because there is no `ALTER` that relocates rows. Any store refuses a redefined key.
Each refusal names what to do instead.

## `eqdata drift`

```bash
eqdata drift --project src/Shop.Api
```

Opens the database and says how it differs from the model:

```
The postgresql database and the model disagree:

  orders  (Shop.OrderData)
    reference is varchar(50), and the model expects varchar(200)
```

This is the question a migration history cannot answer. History records which changes *ran*; it says
nothing about a column altered by hand on staging, a migration that stopped halfway, or an
environment restored from a backup older than the last release. Only looking answers those.

It exits non-zero when the difference is one the application would fail on, so it works as a
deployment gate:

```bash
eqdata drift --project src/Shop.Api || exit 1
```

A column the model does not map is reported but does not fail the check — databases get shared, and
another application's column is not yours. Tables you do not map are not read at all.

`drift` also mentions, separately, when the model has moved beyond the committed snapshot. That is
not drift: the database is behind the code on purpose until a migration runs. It is worth saying
because the two are easy to confuse when reading a failure.

### What it checks, and what it does not

Only the relational providers read their own catalogue today — PostgreSQL, MySQL, MariaDB and SQL
Server. They compare every mapped table and column, each column's type, and its nullability. The
document stores have no schema to introspect, so `drift` reports that it cannot answer for them
rather than answering wrongly.

One thing worth knowing about nullability: the engine's `CREATE TABLE` writes no `NOT NULL` for
ordinary columns — only the primary key is required, and that by the store's own rule. So `drift`
expects every non-key column to be nullable, and a column that *has* a `NOT NULL` is reported as
having been tightened by hand. It is the constraint that starts rejecting writes your code allows,
so it is worth knowing about either way round.
