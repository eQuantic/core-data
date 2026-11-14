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

`--output` (`-o`) puts them in another folder inside the project, such as `Data/Migrations`, and its
segments name their namespace (`Shop.Api.Data.Migrations`). It reads the same on every system: `/` and
`\` both separate the segments, and a leading, trailing or doubled separator is ignored. A segment that
cannot be a C# identifier is made one the way an IDE names a class added to that folder (`schema-v2`
becomes `schema_v2`, `2026` becomes `_2026`, `class` becomes `@class`), and a folder outside the project
(`..`, a drive) is refused.

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

These trigger it:

| | |
|---|---|
| A member added with no value declared | every existing record would take `default(T)` — a value nobody chose |
| A member that appeared while another disappeared, undeclared | generating drop-and-add loses the data (see below) |
| Dropping a collection | it deletes everything in it, and a model diff cannot tell whether that data is finished with |
| An enum switched between its numbers and its labels | a type conversion writes the number `1` as the text `"1"`, never as its label (see below) |
| A label added, renamed, removed or moved in an enum stored as labels, where the store holds them | an enum type, an `ENUM(…)` or a `CHECK` lists the labels, and stored rows hold the old ones (see below) |
| A member that becomes store-generated, changes what the store assigns, or stops being so | no operation makes a store start assigning a member, and a sequence has to start above what is already stored (see below) |
| A key the store starts or stops numbering | an identity cannot be added to or taken off a column the same way on every engine (see below) |
| A search index declared, dropped, or matching another way (its mode, `CaseInsensitive` turned on or off) on Cassandra, or declared or dropped on PostgreSQL | the index already built keeps matching as it was built: it has to be created, dropped or rebuilt (see below) |
| A store-generated member added on MongoDB, Cosmos DB or Cassandra | the provider fills new inserts only, so the records already there need values, and a counter has to start above them |
| A type change no store operation performs | `ConvertField` covers strings, numbers, booleans, dates and identifiers; anything else is yours to write |

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

The declared value is written into the generated file as a C# literal that compiles back to it. An
enum member is written by name, its type spelt as C# spells it (`Shop.Order.Status.Active` for an enum
nested in `Order`); a combination of flags, or a number no member has, as a cast of its number
(`(Shop.Order.Status)42`, `(Shop.Order.Status)(-1)`). A `float` or `double` that is not finite is written
as the constant that names it (`double.NaN`, `float.PositiveInfinity`). A value with no literal form,
an enum declared inside a generic type among them, is refused by name, so the step is written by hand.

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

### Enums stored as labels, and members the store assigns

Two declarations change what the store holds without changing the member's type: `[EnumType]` (or
`Enum<TEnum>()`), which stores an enum as its labels instead of its number, and `[StoreGenerated]` (or
`Generated(...)`), which leaves a member to the store on insert. The snapshot records both, because a
comparison that read only the CLR type would see nothing move while every stored number became
unreadable, or while the store went on not assigning a member the application stopped writing:

```csharp
new FieldSnapshot("State", "state", "labels:Shop.OrderState"),        // an enum stored as its labels
new FieldSnapshot("History", "history", "labels:Shop.OrderState[]"),  // an array of them (PostgreSQL, MongoDB, Cosmos DB)
new FieldSnapshot("Number", "number", "System.Int64") { Generated = "sequence" },
new FieldSnapshot("StampedAt", "stamped_at", "System.DateTime") { Generated = "clock" },
new FieldSnapshot("IssuedAt", "issued_at", "System.DateTime") { Generated = "default now()" },  // relational SQL default
new FieldSnapshot("State", "state", "labels:Shop.OrderState")
{
    Labels = [new LabelSnapshot("Queued", 0, "queued"), new LabelSnapshot("InProgress", 1, "in_progress")],
},
```

`Labels` records each label with the member it stands for and that member's number, in the enum's order:
how a comparison tells a renamed label (the same member, or the same number once the member's name moved)
from one removed while another is added. Two members that swap their labels are a change too, though the
list reads as it did: every row already stored would read back as the other member. A member renamed in
code with its label kept is none. `Generated` and `Labels` are written only when there is one, so
a model without them writes the file it always wrote.

An enum stored as its number keeps the spelling it always had: its CLR type on the relational stores,
MongoDB and Cosmos DB, its integral CQL type on Cassandra. A snapshot committed before these were
recorded has no `Generated` anywhere, which is what every member was then, and no `Labels`, which a
comparison reads as "not known" rather than as every label removed: it still compiles, and an
unchanged model still compares clean against it.

Neither change is ever generated as a step. Switching an enum between its numbers and its labels is
reported as a conversion and written as a `#error` that says what the step must do on that store,
because each one needs something different:

| Store | Numbers to labels (labels to numbers is the reverse) |
|---|---|
| PostgreSQL | create the enum type, then `ALTER COLUMN … TYPE … USING` an expression that maps each number to its label |
| MySQL · MariaDB | go through text: a number put straight into an `ENUM` is read as a label's *position*, counted from 1 |
| SQL Server | change the column to `nvarchar`, rewrite each number as its label, then add the `CHECK` on the labels |
| MongoDB · Cosmos DB | rewrite the number in every document (Cosmos DB: a read and a replace each); the labels' reader refuses a number |
| Cassandra | no column changes type, and no dropped column comes back with another: map the member to a new text column, copy each row across as its label, drop the old one |

`EnumLabels.Of(typeof(TEnum))` pairs each member's name with its label, for writing that step.

A member that becomes store-generated, changes what the store assigns, or stops being so is reported
as its own change, `ChangeGeneration`, and handed back the same way:

| | Relational | MongoDB · Cosmos DB · Cassandra |
|---|---|---|
| becomes a sequence | an identity (PostgreSQL), a sequence object and its default (SQL Server, MariaDB), or `AUTO_INCREMENT` (MySQL), **starting above the largest value already stored** | set the provider's counter (`Explain()` prints it) to the largest value already stored before the first insert |
| becomes the clock, or a SQL default | give the rows holding `NULL` a value, then the column its `DEFAULT` and `NOT NULL` | nothing in the store changes; backfill the documents or rows that must have a time |
| stops being store-generated | drop the identity, sequence or default and the `NOT NULL` | nothing in the store changes; a sequence's counter can be deleted |

```csharp
#error 'Shop.Order.Number' is now numbered by a sequence the store keeps (it was written by the application). Nothing generated makes the store number it: write a Run step that makes it an identity starting above the largest value already stored …
```

A sequence that starts below what is stored hands out numbers already taken, and only a look at the
data says where it has to start — which is why this is a decision and not a step.

A **key** the store starts or stops numbering (`Key(x => x.Id, generated: true)`) is its own change,
`ChangeKeyGeneration`: PostgreSQL adds or drops the identity (starting above the largest key), MySQL and
MariaDB modify the key column with or without `AUTO_INCREMENT`, and SQL Server, which cannot put an
`IDENTITY` on an existing column or take one off, rebuilds the table and copies the rows across under
`IDENTITY_INSERT`.

A store-generated member **added** asks for no value: on every relational engine the store fills the
rows already in the table itself (an identity or `AUTO_INCREMENT` numbers them, and a `NOT NULL`
column added with a sequence's, a clock's or a declared default takes it in every row), so the
generated `AddField` is all there is. One catch, measured on MariaDB 10.10: the `ALTER` that fills a
column defaulting to a sequence's next value is refused ("ALTER command denied … for table
`<table>_<column>_seq`") to a user holding every privilege on the database, and runs for root; run
that migration as a user with global privileges. MongoDB, Cosmos DB and Cassandra assign it on insert only: the
documents or rows already there lack it, so the migration hands back a decision to give them values,
and, for a sequence, to start its counter above the largest of them.

### When an enum's labels change

The labels of an enum stored as its labels are in the store's schema on the relational engines and
only in the data elsewhere, so a label added, renamed, removed or moved is a change of its own,
`ChangeLabels`, and its `#error` names the exact step:

| Store | Added | Renamed | Removed or moved |
|---|---|---|---|
| PostgreSQL | `ALTER TYPE … ADD VALUE 'paused' AFTER 'queued'` (not usable in the transaction that adds it) | `ALTER TYPE … RENAME VALUE 'old' TO 'new'`: the rows follow | a new type with the new labels, the rows holding a removed label rewritten, every column moved to it `USING` its text, the old type dropped and the new one renamed |
| MySQL · MariaDB | `MODIFY COLUMN … ENUM(…)` with the full new list; every stored value whose label is still listed is kept | through a list holding both, the rows rewritten, then the new list | the rows rewritten first: a stored label the list lacks becomes `''` |
| SQL Server | the `CHECK` replaced (and the column widened if a label outgrew it) | the `CHECK` dropped, the rows rewritten, the new `CHECK` added | the same; a move alone changes nothing |
| MongoDB · Cosmos DB · Cassandra | nothing: no line is written | every stored `'old'` rewritten as `'new'` | every record holding a removed label rewritten (the labels' reader refuses it); a move needs nothing |

On PostgreSQL the type is one object however many columns use it, so its step is said once.

### When a search index's declaration changes

The snapshot records each search index and what it promises (`new SearchSnapshot("Name", "Contains,
case-insensitive")`), and a declaration added, dropped or changed is a change of its own, `ChangeSearch`.
An index the store already built does not follow its declaration, so the `#error` says what to run:

| Store | Declared | Dropped | Matching another way |
|---|---|---|---|
| Cassandra | `.EnsureCollection()` creates the SASI index `<table>_<column>_search` | a Run step with `DROP INDEX IF EXISTS <table>_<column>_search` | the index rebuilt: that `DROP INDEX` in a Run step, then `.EnsureCollection()`, which creates it with the new options |
| PostgreSQL | `.EnsureCollection()` creates the trigram index | a Run step with `DROP INDEX IF EXISTS "ix_<table>_<column>_search"` | nothing: the trigram index serves every mode |
| SQL Server · MySQL · MariaDB · MongoDB · Cosmos DB | nothing: no index is built for the declaration | nothing | nothing |

Until a Cassandra index is rebuilt the provider reads the cluster's schema and pushes a match that ignores
case down only where the index really ignores case; anywhere else the match runs client-side, behind
`.AllowClientEvaluation()`, so no row is missed in the meantime.

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
writes your code allows, so it is worth knowing about either way round. A store-generated column is
the other exception: the store always gives it a value, the engine writes it `NOT NULL`, and that is
what `drift` expects of it.

An enum stored as its labels is compared as the column the dialect gives it — PostgreSQL's enum type,
MySQL's `ENUM(…)` with its labels, SQL Server's `nvarchar` as wide as the longest label — so a column
that still holds the enum's numbers after the model switched to labels (or the other way round) is
reported as a type that differs, and fails the check: every read of it would.
