# Relations and text in filters

A filter can test what an entity is related to, and match text without regard to case — with the
same C# expression on every store, each through its own mechanism.

```csharp
// The items that mention a user, the mention not deleted:
var mentioned = await items.GetFilteredAsync(
    x => x.Mentions.Any(m => m.UserId == userId && m.DeletedAt == null));

// Every artifact of a workspace's repositories, gone in one call (it returns how many):
var deleted = await artifacts.DeleteManyAsync(x => x.Repository.WorkspaceId == workspaceId);

// A name, whatever its case:
var acme = await companies.GetFilteredAsync(
    x => x.Name.Equals("ACME CORP", StringComparison.OrdinalIgnoreCase));
```

## Filters across relations

A filter accepts, over the navigations the model declares (the ones [includes](includes.md) follow):

| You write | It means |
|---|---|
| `x.Items.Any()` / `!x.Items.Any()` | the entity has related rows / has none |
| `x.Items.Any(i => …)` / `!x.Items.Any(i => …)` | some related row meets the condition / none does |
| `x.Items.All(i => …)` | every related row meets it (true when there are none) |
| `x.Repository.WorkspaceId == w` | the referenced row meets the test — any test that reads only that row: a comparison, a string match, a call |
| `x.Repository.Space.WorkspaceId == w` | the same, one reference further |
| `x.Container == null` / `!= null` | there is no referenced row (the foreign key unset, **or naming no row**) / there is one |

They combine with the entity's own members under `&&`, `||` and `!`, and they apply to **reads,
counts, `UpdateManyAsync`, `DeleteManyAsync` and `UpdateAndGetAsync`** alike. A related entity's
global filter (its tenant scope, its soft-delete) applies to the related rows, as it applies to a read
of that entity.

A navigation is the one an include would load: a reference whose foreign key the entity holds
(`{Nav}Id` by convention), or a collection whose rows hold a foreign key back to it (`{Entity}Id`);
on the relational engine `Reference(…)`/`Collection(…)` declare others, on the document stores and
Cassandra `[ForeignKey("Owner")]`. A class-typed member with no foreign key is an embedded document,
filtered as one (`x.Address.City == …`). Both entities are registered in the model.

### How each store runs it

| Store | A read or a count | A set-based write |
|---|---|---|
| PostgreSQL, SQL Server, MySQL, MariaDB | a **correlated `EXISTS`** (`NOT EXISTS` for `!Any`, `All` and `== null`) in the statement's own `WHERE` — one statement, inside the transaction | the same `EXISTS` in the `UPDATE`/`DELETE` — one statement; on MySQL and MariaDB, when the relation reads the written table itself, the matching keys are read first with `FOR UPDATE` and the write runs by them (below) |
| MongoDB | the **related keys resolved first**: one aggregation over the related collection per test (`$match`, then `$group` on the key), inside the open transaction's session, then `$in` (`$nin`) over them; past 10,000 keys or 1 MiB of them, a **`$lookup`** of a related document meeting the test, in the read's own aggregation | the same: the keys first, then `updateMany`/`deleteMany`/`findOneAndUpdate` filtered by them — a write's filter cannot hold a `$lookup`, so past the bound one aggregation with it reads the matching ids, and the write runs over them in batches |
| Cosmos DB | the **related keys resolved first**: one `SELECT DISTINCT VALUE` over the related container per test (cross-partition), then `IN` (`NOT IN`) over them | the same; each item is then patched (or deleted) **under the filter holding those keys** |
| Cassandra | the **related keys resolved first**, through the same planner and gates as any read and **under the read's own opt-ins** — a related read that names none of the related table's partitions (a test with no condition among them) is a scan, behind `.AllowFiltering()` — then `IN` over them where the table's key serves it, in chunks of 100 keys — `NOT IN` and anything else CQL cannot express runs client-side behind `.AllowClientEvaluation()` | the keys first, then the write finds its rows by them as by any key: a write carries no opt-ins, so its related read has to be partition-scoped — by the relation's condition, or by the rows the write finds when its filter names the written table's partition — and is refused otherwise; a negated test leaves out, of the rows the rest of the filter finds, those whose key the related read returned, and writes each other row by its full key in a lightweight transaction |

```sql
-- relational: x => x.Mentions.Any(m => m.UserId == @u && m.DeletedAt == null) && x.Container == null
SELECT … FROM "items"
WHERE (EXISTS (SELECT 1 FROM "mentions" "eq_rel0"
               WHERE "eq_rel0"."item_id" = "items"."id"
                 AND ("eq_rel0"."user_id" = @p0 AND "eq_rel0"."deleted_at" IS NULL))
   AND NOT (EXISTS (SELECT 1 FROM "containers" "eq_rel1" WHERE "eq_rel1"."id" = "items"."container_id")))
```

`Explain()` names the mechanism: the relational plan shows the `EXISTS` and a note per relation; the
other stores' plans note what is read first and from where (the key lists show empty there — an
explain runs nothing).

### What each store costs, and what it guarantees

- **Relational** stores join on the server: the subquery is correlated on the foreign key, so an
  index on it (the migrations' `Index(x => x.ItemId)`) makes each test an index probe. The relation
  is evaluated by the statement itself, under the isolation of the transaction it runs in.
- **MongoDB, Cosmos DB and Cassandra** read the related keys **before** the filtered read or write,
  and **every key travels** into its `IN` — a test that matches many related rows makes a long list.
  `!x.Runs.Any()` and `x.Container == null` read the keys of *every* related row (over the whole
  collection, container or table), so they cost a full read of the related keys, which Cassandra
  makes only behind `.AllowFiltering()`, and a Cassandra write only for the rows it finds. MongoDB stops
  reading them past 10,000 keys or 1 MiB and joins the related documents with a `$lookup` in the read's
  own aggregation instead, one lookup per owner document — index the related key, as on the relational
  stores (see [MongoDB](../providers/mongodb.md#filters-across-relations-in-or-lookup)).
- The keys and the read are **two operations**, not one snapshot: a related row written between them
  is seen or not by chance. MongoDB reads both inside the open transaction's session, so inside an
  explicit transaction they are one snapshot; Cosmos DB and Cassandra have no read transaction to
  hold them together. On Cosmos DB a set-based write's condition holds the resolved keys: an item
  is written only while its foreign key is still one of them.
- **Cosmos DB** reads the related container cross-partition unless its partition key pins it, and a
  container shared by several entity types returns every type's documents to the key read — give
  each related entity its own container, or a global filter that tells its documents apart.
- **Cassandra** applies its gates to the related read: a read that names none of the related
  table's partitions — a test with no condition (`x.Runs.Any()`, `!x.Runs.Any()`,
  `x.Container == null`) reads every related row's key — or an `IN` the related table's key does not
  serve needs `.AllowFiltering()`, a condition CQL cannot express (`!=`, a null test, `NOT IN`) needs
  `.AllowClientEvaluation()` — both on the read's own options, which the related read inherits, and
  checked before anything is read. Model the related table partitioned by the key the filter names,
  and the relation is one routed query; the `IN` the keys make runs in chunks of 100 keys, a query
  each. A set-based write carries no opt-ins, so its related read has to be partition-scoped: by the
  relation's condition, or — when the write's filter names the written table's partition — by the
  rows the write finds, their keys read from the related table by its partition key
  (`x.Space == s && !x.Runs.Any()` reads only the runs of the items of `s`); any other is refused. A
  set-based write with a negated relation cannot put `NOT IN` in a lightweight transaction's `IF`, so
  it reads the rows the rest of its filter finds (under the written table's partition, which the
  filter has to name), leaves out those whose key the related read returned, and writes each other row
  by its full key, `IF` the filter's other conditions still hold and every column the negated test read
  (`ContainerId`) still holds the value read — `IF EXISTS` when there is nothing else to hold. A row it
  meets that was not among the rows its relation was read for is left alone. The count is the
  transactions Cassandra applied.
- **MySQL and MariaDB** refuse an `UPDATE`/`DELETE` whose subquery reads the table being written
  (error 1093), as a self-referencing relation does (a node filtered by its children). There a
  set-based write reads the matching keys first, inside the operation's transaction (the open one, or
  its own), with `SELECT … FOR UPDATE` — a `SELECT` may read its own table — so the set cannot change
  before the write; the `UPDATE`/`DELETE` then runs by those keys, the filter's other conjuncts still
  applied. PostgreSQL and SQL Server keep the single statement.

A relation test compares the related rows with **values**: one that also reads the entity's own
members (`x.Items.Any(i => i.OwnerId == x.Id)`) is refused with the reason, and so is a collection
navigation used otherwise than through `Any`/`All` (`x.Items.Count > 0`).

## Text without regard to case

| You write | It matches |
|---|---|
| `string.Equals(x.Name, v, StringComparison.OrdinalIgnoreCase)`, `x.Name.Equals(v, …IgnoreCase)` | the whole text, whatever the case |
| `x.Name.Contains(v, …IgnoreCase)`, `StartsWith`, `EndsWith` | a part of it, whatever the case |
| `x.Name.ToLower() == v`, `x.Name.ToUpper().StartsWith(v)` (and `Contains`, `EndsWith`, `Equals`) | the member's lower- or upper-cased text against the value **as it is** — as in C#, `ToLower() == "ACME"` matches nothing |

`InvariantCultureIgnoreCase` reads as `OrdinalIgnoreCase`. The value's wildcard characters are
**literal** on every store (`%`, `_`, `[`, a regular expression's metacharacters), escaped where the
store needs it. A one-argument `Contains`/`StartsWith`/`EndsWith` keeps following the store's
collation, as equality does.

| Store | Mechanism | Cost |
|---|---|---|
| PostgreSQL | `lower(col) = lower(@p)`; `col ILIKE @p ESCAPE '!'` for a part of it | an expression index on `lower(col)` serves equality; a declared search index (GIN trigram) serves `ILIKE` |
| SQL Server | `LOWER(col) = LOWER(@p)`, `LIKE` with `%`, `_` and `[` escaped | a `LOWER()` call defeats a plain index; a persisted computed column can carry one |
| MySQL / MariaDB | `LOWER(col) = LOWER(@p)`, `LIKE … ESCAPE '!'` | as SQL Server; the default collations also ignore accents, so `é` matches `e` under them |
| MongoDB | an escaped, anchored, case-insensitive regular expression (`{ name: /^acme corp\z/i }`) | a case-insensitive regular expression scans the index (or the collection): it cannot seek |
| Cosmos DB | `STRINGEQUALS`, `CONTAINS`, `STARTSWITH`, `ENDSWITH` with their case-insensitive flag (`LOWER`/`UPPER` for the folded forms) | a case-insensitive comparison scans (the index, or the documents of the query's scope) where a case-sensitive one seeks: its RU charge follows what it scans |
| Cassandra | a `LIKE` on a SASI index the member declares case-insensitive (`SearchIndex(x => x.Name, CassandraSearchMode.Contains, caseInsensitive: true)`, or `[SearchIndex(CaseInsensitive = true)]`) | the index serves the match; elsewhere — no such index, a literal `%` in the value (SASI's `LIKE` has no escape), a mode that cannot serve the position — the test runs client-side behind `.AllowClientEvaluation()` |

On Cosmos DB a set-based write's patch carries the filter as its condition; where the store cannot
evaluate a function there (the emulator refuses `STRINGEQUALS` in a patch condition), each item is
patched under the `_etag` of a read that still finds it matching — the same compare-and-set, in two
round trips.

## An array's `Contains`

`ids.Contains(x.Id)` is an `IN` on every store, whether `ids` is a `List<T>` or an array. Under
C# 14 an array's `Contains` binds to `MemoryExtensions.Contains` over a `ReadOnlySpan<T>`, which a
LINQ provider (Cosmos DB's) cannot evaluate and an expression compiler cannot always run; the engine
rewrites it to `Enumerable.Contains` before the expression reaches a driver or the client-side
residual, so both spellings run the same everywhere. On Cassandra, an `IN` on a column outside the
primary key — which CQL refuses — runs client-side behind `.AllowClientEvaluation()`.
