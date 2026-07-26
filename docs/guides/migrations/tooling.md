# Generating migrations, and checking for drift

`eqdata` writes migrations for you by comparing the model against a snapshot committed beside it, and
tells you when a database has stopped matching the model. It generates ordinary source you read and
edit — it does not run anything, and it does not know what your data means.

Both work on **all six stores** — though each store can be asked only what it actually keeps, so what a
drift check compares differs by store. See [what each store can answer](#what-each-store-can-answer).

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

### When the model lives in a library

The tool runs an application, and only an application can be run: a class library produces no
`runtimeconfig.json`, and without one the assemblies your model depends on cannot be located. So the
two roles are named separately.

```bash
eqdata migrations add AddCustomerTier \
  --project        src/Shop.Data \
  --startup-project src/Shop.Api
```

`--project` is where the migrations belong and whose namespace they take. `--startup-project` is what
gets run. Both are searched for the `IDesignTimeServices` and for the snapshot, so it does not matter
which project you put them in. Pass neither and the current directory does both jobs, which is right
when the model lives in the application.

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
| Dropping a collection | it deletes everything in it, and a model diff cannot tell whether that data is finished with |

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

The attribute works on **all six stores**; the fluent form is relational, where the model has a
column to hang it on. On a document store the attribute is the only place the answer can live — the
class *is* the schema — which is also why it does more there than remove a diagnostic.

### Adding a member on a document store

A collection or container needs no declaration to accept a new field: documents gain one on write.
The documents already there are the problem. Absent the field, deserialization hands your application
`default(T)` — a `0`, an `Unspecified` date, the first value of an enum — and none of those is
distinguishable from a value somebody meant.

So on MongoDB and Cosmos DB, `AddField` **writes the declared default into the documents that predate
the member**:

```csharp
public sealed class Ledger
{
    [DefaultValue("web")] public string Channel { get; set; } = "";
}

migration.For<Ledger>(ledger => ledger.AddField(x => x.Channel));
// every document without `channel` now has "web"; the ones that had a value keep it
```

Declare nothing and it stays a no-op, deliberately: an absent field is at least visible, and a value
nobody chose is not. Cosmos has no set-based update, so this costs one read and one patch per
document that lacks the field — the query filters on absence for exactly that reason.

Cassandra needs none of this. It has a real schema, so `AddField` is an `ALTER TABLE ... ADD` and a
missing value reads as null.

### Refusals

Some changes are not generated at all, and nothing is written when one appears — generating the rest
would move the snapshot past a change that never ran. Cassandra refuses a moved partition or
clustering key, because there is no `ALTER` that relocates rows. Any store refuses a redefined key.
Each refusal names what to do instead.

Two more are refused when a migration runs rather than when it is generated, because they are things
one store cannot do and another can: Cassandra will not rename a table, and Cosmos DB will not rename
a container — both fix the name at creation. Resizing a field is a no-op on MongoDB and Cosmos DB,
deliberately: a document's field is as big as its value, and one migration is written for six stores,
so a step that means nothing on one of them must not throw there.

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

### What each store can answer

A drift check can only compare what a store keeps, and they keep different amounts.

| Store | What is compared |
|---|---|
| PostgreSQL · MySQL · MariaDB · SQL Server | every mapped table and column, each column's type and nullability |
| Cassandra | every mapped table and column, each column's CQL type, **and the partition key** |
| Cosmos DB | the containers and **the partition key paths** they were created with |
| MongoDB | the collections and **their indexes** |

No field is ever compared on MongoDB or Cosmos DB, and none is claimed: a document either carries a
property or it does not, and sampling documents would describe the ones that came back rather than the
collection.

**On MongoDB the index that matters is the TTL one.** A time-to-live declaration is delivered as an
index, and without it nothing expires — documents that should have been deleted are still being read.
That is the one index whose absence changes what the store holds, so it is the one that fails the
check. Every other index changes how fast a query runs, not whether it answers, so a missing or
differing one is reported without failing the gate. An index nobody declared is reported and ignored,
like a column nobody mapped.

Sharing a Cosmos container between entity types is the idiom, not a mistake: the check reads it as one
container named after all the types that map to it, so a difference is reported once rather than once
per type.

**The partition key is the finding worth having.** It is fixed when a table or container is created,
so a different one cannot be migrated at all — only rebuilt alongside and copied into. The report
says that outright when it sees one, because it is a thing to learn from a check rather than from a
deployment.

One thing worth knowing about nullability on the relational stores: the engine's `CREATE TABLE`
writes no `NOT NULL` for ordinary columns — only the primary key is required, and that by the
store's own rule. So `drift` expects every non-key column to be nullable, and a column that *has* a
`NOT NULL` is reported as having been tightened by hand. It is the constraint that starts rejecting
writes your code allows, so it is worth knowing about either way round.
