# Upserts and write outcomes

"This row, whether or not it exists yet", "these rows, except the ones already there", "that key is
taken", "how many rows did my condition match", "what does the row hold now". Each is one call that
means the same thing on every engine, through the engine's own mechanism — and where an engine's
guarantee is weaker, the tables below say so.

Upserts, inserts skipping duplicates and returning updates run **at once** (they are not staged for
the next `Commit`) and join an open [explicit transaction](transactions.md) on the relational stores
and MongoDB. On Cosmos DB and Cassandra an explicit transaction is a batch of the *staged* writes,
deferred to its commit, which a write that has to read first cannot join: there they run on their
own. The member-scoped `Modify` is staged, like any `Modify`, and commits with the rest. They live on
capability interfaces every provider's repository implements — cast to them, or resolve them from DI:

| Interface | What it adds |
|---|---|
| `IUpsertRepository<TEntity, TKey>` | `UpsertAsync`, `InsertManyAsync(items, skipDuplicates)` |
| `IReturningUpdateRepository<TEntity, TKey>` | `UpdateAndGetAsync(filter, update)` — a set-based update that returns the rows it wrote |
| `IPartialModifyRepository<TEntity, TKey>` | `Modify`/`ModifyAsync(entity, members)` — a staged update of the named members only |

```csharp
var upserts = (IUpsertRepository<Secret, Guid>)repo;        // or inject IUpsertRepository<Secret, Guid>
```

## Upserts

```csharp
// On the key: insert the entity, or overwrite the row's non-key members with it.
var stored = await upserts.UpsertAsync(secret);

// On a unique key, with a set-based update for the existing row:
var stored = await upserts.UpsertAsync(
    new Secret { WorkspaceId = w, Name = "token", Value = "a" },
    on: x => new { x.WorkspaceId, x.Name },
    update: x => new Secret { Value = "b", Uses = x.Uses + 1 });

// On a partial unique index (the rows not soft-deleted):
var stored = await upserts.UpsertAsync(label, x => x.Name, conflictFilter: x => x.DeletedAt == null);
```

What an upsert promises, everywhere:

- **One row, never a lost race.** Two upserts of the same target at the same time leave one row and
  neither fails on that target — *provided the store holds the target unique* (a unique index or
  constraint, the primary key, a Cosmos DB unique key). Without that, the guarantee is the store's,
  and the table says what that is.
- **The row as stored comes back**, including what the store assigned — the existing row keeps its
  key (an upsert on `WorkspaceId, Name` never changes the row's `Id`).
- **The insert is an insert, the update an update.** The insert takes the store's values for
  [store-generated](../modeling/annotations.md) members and the insert's lifecycle stamps
  (`CreatedAt`); the update never writes store-generated members or the creation stamps, and stamps
  `UpdatedAt` — see [Lifecycle](lifecycle.md). A concurrency token is bumped on the update.
- **The update** is `update` applied to the existing row (assignments that read the row, such as
  `x.Uses + 1`, read the *existing* row), or, without one, the entity's mapped non-key members.
- **A key or unique key other than the target** that the insert would take fails with
  `DuplicateKeyException` — the upsert does not silently turn into an update of some other row.

| Store | Mechanism | Guarantee |
|---|---|---|
| PostgreSQL | `INSERT … ON CONFLICT (target) [WHERE filter] DO UPDATE … RETURNING` | atomic, race-free; the target needs a matching unique index or constraint (PostgreSQL refuses one it cannot infer), and the conflict filter must imply a partial index's predicate |
| SQL Server | `MERGE … WITH (HOLDLOCK) … OUTPUT INSERTED` | atomic, race-free — `HOLDLOCK` keeps the target's range locked from the match to the insert |
| MySQL / MariaDB | the target read with `SELECT … FOR UPDATE` in a transaction, then `INSERT` or `UPDATE` by key; a duplicate key on the target (a concurrent insert won) or a deadlock runs it again | atomic, race-free with a unique index on the target. `ON DUPLICATE KEY UPDATE` is not used: it fires on *any* unique key and cannot be told which one is the target |
| MongoDB | `findOneAndUpdate` of the target (returning the document after it); when nothing matches, an upsert of `$setOnInsert` — and when that finds the target after all, the update runs again | race-free with a unique index on the target (the losing insert gets E11000 and becomes the update); **without one, MongoDB itself can insert twice** |
| Cosmos DB | the target read (by id, or by a query inside the entity's partition), then a replace (or a patch of the update) under its `_etag`, or a create; a 412 or 409 reads it again | race-free on the id and on a container's unique key, both of which hold **within a partition**; a target nothing holds unique can be created twice |
| Cassandra | `INSERT … IF NOT EXISTS`, then `UPDATE … IF EXISTS` (or `IF column = value read`, for a value computed from the row) — lightweight transactions | race-free on the **primary key, the only key Cassandra holds unique**: an upsert on any other members is refused (`NotSupportedException`), and a conflict filter the stored row does not match is a `DuplicateKeyException` |

Cassandra's lightweight transactions are Paxos rounds: an upsert costs two of them when the row
exists, and under contention one can time out with its outcome unknown. The provider settles it with
a serial read: a write of values set outright is written again if it did not apply; a value computed
from the row (`x.Uses + 1`, the version) is never written twice, so that timeout reaches you as a
`TimeoutException` with the driver's inside.

## Inserting a batch, skipping the rows already there

```csharp
long inserted = await upserts.InsertManyAsync(items, skipDuplicates: true);
```

Every entity whose key and unique keys are free is inserted; one whose key or unique key is taken —
by a stored row, or by an earlier entity of the batch — is left out without failing, and the count
says how many went in. With `skipDuplicates: false`, a taken key fails the call with
`DuplicateKeyException`. Lifecycle stamps apply as they do to a staged insert; the values the store
assigns are not read back onto the entities (read the rows if you need them).

| Store | Mechanism | Atomic? |
|---|---|---|
| PostgreSQL | multi-row `INSERT … ON CONFLICT DO NOTHING` | **yes** — one transaction; without skipping, nothing is written |
| SQL Server | one `INSERT` per row in `TRY…CATCH` swallowing only errors 2627/2601 (a duplicate leaves the transaction committable) | **yes** |
| MySQL / MariaDB | one `INSERT` per row in a transaction, discarding only error 1062 | **yes** |
| MongoDB | `insertMany`, unordered when skipping (discarding only E11000), ordered otherwise | no: an ordered insert stops at the taken key and the documents before it stay — unless an explicit transaction is open (where MongoDB aborts the transaction on any write error, so a duplicate fails the call even when skipping) |
| Cosmos DB | one create per item, concurrently, discarding only 409 | per item: without skipping, the call fails after every create has answered, and the others stay |
| Cassandra | one `INSERT … IF NOT EXISTS` per row, concurrently | per row, as above |

MySQL and MariaDB do not use `ON DUPLICATE KEY UPDATE` here either: the driver reports the rows a
statement *found* (which matched counts need), and under that a skipped row and an inserted one both
count one — the number of rows inserted cannot be read back.

## `DuplicateKeyException`

A store that refuses a write because a key or a unique key is taken answers, on every engine and
every write path — a staged insert or update at commit, a bulk load, a set-based update, an upsert
that lands on another unique key, `InsertManyAsync` without skipping — with one exception:

```csharp
try { await uow.CommitAsync(); }
catch (DuplicateKeyException taken) when (taken.ConstraintName == "ix_users_email")
{
    return Conflict("That e-mail is already registered.");
}
```

`ConstraintName` is what the store named; `InnerException` is the driver's own exception.

| Store | Read from | `ConstraintName` |
|---|---|---|
| PostgreSQL | SQLSTATE `23505` | the constraint or index |
| SQL Server | errors 2627 and 2601 | parsed from the message |
| MySQL / MariaDB | error 1062 (a bulk load's duplicate-key warning too: the load is rolled back) | parsed from the message (MySQL's `table.` prefix dropped) |
| MongoDB | E11000, from a write, a bulk write or a command | the index |
| Cosmos DB | 409 on a create (a taken id or unique key), in a transactional batch too | none — Cosmos DB does not say |
| Cassandra | an `INSERT … IF NOT EXISTS` that did not apply | none |

Cassandra's plain insert overwrites by definition (an `INSERT` is an upsert there), so a staged `Add`
of a taken key is not a refusal: the refusals are those of its lightweight transactions —
`InsertManyAsync`, and `AddIfNotExistsAsync`, which answers `false`.

## A set-based update that returns its rows

```csharp
var rows = await ((IReturningUpdateRepository<ItemType, Guid>)repo).UpdateAndGetAsync(
    x => x.Id == id,
    x => new ItemType { NextSequence = x.NextSequence + 1 });
var issued = rows.Single().NextSequence;      // incremented and read in one atomic call
```

The update applies as `UpdateManyAsync` does — at once, each row only while it still matches the
filter — and every row it updated comes back as stored after it, store-generated members included.

| Store | Mechanism | Atomic? |
|---|---|---|
| PostgreSQL | `UPDATE … RETURNING` | yes |
| SQL Server | `UPDATE … OUTPUT INSERTED` | yes |
| MySQL / MariaDB | `SELECT … FOR UPDATE` of the keys, `UPDATE` by key, then the rows read back — one transaction (MariaDB has `RETURNING` for an insert, not for an update) | yes |
| MongoDB | the matching ids, then `findOneAndUpdate` per document (filtered on its id *and* the filter), returning it after the update | per document; the set is one unit only inside an explicit transaction |
| Cosmos DB | a patch per matching item under the filter (`FilterPredicate`), which answers with the item | per item |
| Cassandra | a lightweight transaction per row: `UPDATE … IF EXISTS` (or `IF` the filter's other members), and `IF column = value read` for a value computed from the row; the row returned is the row read with the update's values | per row |

## Updating only the members you changed

```csharp
page.Title = "final";
await ((IPartialModifyRepository<Page, Guid>)repo).ModifyAsync(page, x => x.Title);
// or several: x => new { x.Title, x.Locked }
await uow.CommitAsync();
```

A full `Modify` writes every column, so two requests that each changed a different member of one row
write back each other's stale value — the last commit undoes the first. `ModifyAsync(entity, members)`
stages an update of the named members (plus the concurrency token and the `UpdatedAt`/`UpdatedById`
stamps the model declares) and leaves every other stored member as it is: both requests keep their
change. Naming the key, a store-generated member or the token is refused.

| Store | The staged write |
|---|---|
| Relational | `UPDATE … SET named columns WHERE key [AND version = @old]`, in the commit's transaction |
| MongoDB | `updateOne` with a `$set` of the named elements (filtered on the version read, with a token) |
| Cosmos DB | a patch of the named paths (under `If-Match` with a token) — at most ten paths, Cosmos DB's limit |
| Cassandra | `UPDATE … SET named columns WHERE key` (`IF version = ?` with a token); Cassandra's `UPDATE` is an upsert, so a row deleted meanwhile is written again, with those columns only |
