# Named locks

A named lock serialises work on a **name** across processes — `tenant:<user>` around provisioning a
tenant, `item:<id>` before writing an item's children — without writing the store's own locking SQL.
One call means the same thing on every store: one holder at a time per name, names independent of
each other. How each store holds it, and what that guarantees when a holder dies, is in the table
below.

## Taking one

Every unit of work implements `ILockingUnitOfWork` (and the DI extensions register it, on the AOT
paths too):

```csharp
var locks = (ILockingUnitOfWork)unitOfWork;       // or inject ILockingUnitOfWork

await unitOfWork.BeginTransactionAsync();
try
{
    await locks.AcquireLockAsync($"item:{itemId}", TimeSpan.FromSeconds(10));
    // … read the item, write its children …
    await unitOfWork.CommitTransactionAsync();       // releases the lock
}
catch
{
    await unitOfWork.RollbackTransactionAsync();     // so does this
    throw;
}
```

Outside a transaction, the handle holds the lock until it is disposed:

```csharp
await using (await locks.AcquireLockAsync($"tenant:{userId}", TimeSpan.FromSeconds(10)))
{
    // … provision the tenant …
}
```

The rules are the same on every store:

- **Inside an explicit transaction** the lock is held until the transaction commits or rolls back,
  whether or not the handle is disposed first. **Outside one**, until the handle is disposed.
  Disposing the unit of work releases whatever it still holds. A commit or a rollback that **fails**
  still ends the transaction, and the locks taken inside it: a failed commit rolls back what it had
  not made durable, and the next acquisition of the name asks the store again. A key the store refuses
  at `COMMIT` (a PostgreSQL constraint declared `DEFERRABLE INITIALLY DEFERRED`) is a
  `DuplicateKeyException`, as it is from any other write.
- **A timeout** throws `TimeoutException`. `TimeSpan.Zero` tries once; `Timeout.InfiniteTimeSpan`
  waits until the lock is free or the token is cancelled.
- **Cancellation** throws `OperationCanceledException` and leaves no lock behind. A relational
  statement that takes the lock is never cut off mid-flight (it may or may not have taken the lock);
  its wait is bounded instead, so a cancellation is noticed within one `PollInterval`. A lease write is
  cut off at once, and settled when the acquisition gives up (see [Leases](#how-a-lease-is-written)).
- **Re-acquiring** a name the unit of work already holds succeeds at once, with no second store lock:
  the acquisitions are counted, and the lock goes when the last of them ends — the last handle
  disposed outside a transaction, or the end of the transaction for those taken inside it. A lease
  reported **lost** is not held any more, so it is not re-entered: re-acquiring its name lets it go
  and takes the name afresh from the store, waiting like any other holder, and the handles that held
  the lost lease end with it.
- **Names are compared exactly**, case and all, on every store — including MySQL, whose own lock
  names ignore case (see [Keys](#keys)).
- A unit of work serves one flow at a time, as for every other call. Two units of work — two DI
  scopes — are two holders, even in one process.

## Store by store

| Store | Mechanism | Inside a transaction | Outside one | A holder that dies | Waiting |
|---|---|---|---|---|---|
| PostgreSQL | advisory lock on one `bigint` key | `pg_try_advisory_xact_lock`; the commit or rollback releases it | `pg_try_advisory_lock` on a pinned connection; `pg_advisory_unlock` on dispose | the server releases it when the session ends | polled — a try-lock never aborts the caller's transaction, as a `lock_timeout` would |
| SQL Server | `sp_getapplock`, exclusive, in the current database | `@LockOwner = 'Transaction'`; the commit or rollback releases it | `@LockOwner = 'Session'` on a pinned connection; `sp_releaseapplock` on dispose | the server releases it when the session ends | on the server (`@LockTimeout`), in slices of `PollInterval`; result codes checked (-3, a deadlock victim, throws) |
| MySQL | `GET_LOCK`, server-wide | taken on the transaction's session; **released by the engine** right after the commit or rollback (no transaction releases a `GET_LOCK`) | on a pinned connection; `RELEASE_LOCK` on dispose | the server releases it when the session ends | MySQL rounds its wait to whole seconds, so a wait under a second is `GET_LOCK(name, 0)` asked every `PollInterval`; `NULL` (an error) throws |
| MariaDB | `GET_LOCK`, server-wide | as MySQL | as MySQL | as MySQL | on the server, in slices of `PollInterval` (MariaDB waits fractions of a second) |
| MongoDB | **lease**: a document in `_locks` | written outside the transaction (others must see it), deleted when it ends | deleted on dispose | **the lease expires** `LeaseDuration` after its last renewal, by the server's clock | polled, backing off up to ten `PollInterval`s |
| Cosmos DB | **lease**: an item in `_locks` (partitioned by `/id`) | written outside the transactional batch, deleted once it has run or been discarded | deleted on dispose | **the lease expires** `LeaseDuration`, rounded up to whole seconds, and up to a second more after its last renewal, by the server's clock | polled, backing off up to ten `PollInterval`s |
| Cassandra | **lease**: a row in `equantic_locks`, with a TTL | written outside the `LOGGED BATCH`, deleted once it has run or been discarded | deleted on dispose | **the row's TTL runs out** `LeaseDuration`, rounded up to whole seconds, and up to a second more after its last renewal, by the cluster's clock | polled, backing off up to ten `PollInterval`s |

### How a lease is written

Of two holders racing for a lease, exactly one wins:

| Store | Taken when absent | Taken over when expired | Renewed / released by its owner |
|---|---|---|---|
| MongoDB | one `findOneAndUpdate` upsert on `_id`, whose pipeline keeps another owner's live lease and replaces owner and expiry only when `expiresAt <= $$NOW` or the lease is the writer's own | the same write | updates and deletes filtered on `owner`; a TTL index on `expiresAt` sweeps dead holders' documents |
| Cosmos DB | create, answered 409 while another owner's item is there | when `_ts` + 1 s + its lease is past `GetCurrentDateTime()`, a replace guarded by the item's ETag | replace and delete guarded by the ETag of the owner's last write |
| Cassandra | `INSERT … IF NOT EXISTS USING TTL` (Paxos), the TTL being the lease rounded up to whole seconds plus one | the expired row is gone, so the same insert applies | `UPDATE … USING TTL … IF owner = ?` and `DELETE … IF owner = ?` |

Every attempt of one acquisition writes as **one owner**, so a write the store applied although its
answer was lost is the acquisition's own lease, not a stranger's to wait out:

- **A lost answer is settled by reading the lease back.** A Cosmos DB write answered 408 or 503, or a
  Cassandra lightweight transaction whose coordinator timed out (a `CAS` write timeout, or the
  driver's own), may or may not have landed. The attempt reads the lease (Cassandra at `SERIAL`,
  which finishes or forecloses the round it raced): a lease that names the acquisition is written once
  more under its condition (the ETag, `IF owner = ?`), and held; anything else is another owner's or
  nobody's. A lease found to be the acquisition's own on a conflict (a 409, an insert not applied, a
  live MongoDB lease) is written once more the same way. Either way the lease's term counts from a
  write of the attempt that holds it.
- **Every call keeps to the acquisition's deadline and token.** An attempt is cut off when the timeout
  passes (given at least a second, so `TimeSpan.Zero` still makes one whole attempt) or the token is
  cancelled, however long the store or its retries take.
- **An acquisition that gives up leaves no lease behind.** When an attempt was cut off, or failed before
  it could tell whether its write landed, the acquisition deletes the lease if it names it, waiting up
  to a second for the store, before it throws. A lease it cannot delete, or one the store applies
  later still, runs out within `LeaseDuration`; until then another attempt of the same acquisition
  would recognise it.

**A lease never expires before `LeaseDuration`.** That is the promise a holder is given, and two of the
stores count in whole seconds, which would cut into it. Cosmos DB's `_ts` is the second of the last
write, so the lease is measured from the second after it. Cassandra counts a TTL from the start of the
second the write lands in (the expiry is the write's time in seconds, truncated, plus the TTL), so a
row written late in a second goes almost a second before its TTL: the TTL is the lease rounded up to
whole seconds plus that second. On both, a dead holder may keep the others waiting up to a second and
a fraction past `LeaseDuration`; MongoDB compares milliseconds on the server's clock and has no such
margin. Its TTL index sweeps only expired documents and on its own schedule (every 60 s), which never
expires a lease early; the lease's own `expiresAt <= $$NOW` is what hands it over. So the index is
created best effort: one that cannot be (an index on `expiresAt` with other options is already there,
or the user may not create indexes) is logged as `LeaseSweepFailed` (event 10402), and the locks go on.

A [custom relational dialect](../architecture/extending.md) declares its mechanism through
`SqlDialect.NamedLocks` (the statements that take and release a lock, and whether its server waits);
one that declares none makes `AcquireLockAsync` refuse with `NotSupportedException`, rather than
imitate a lock with a table.

## Sessions and leases do not promise the same thing

A relational lock lives as long as the session (or transaction) that holds it. A holder that dies
takes its session with it, and the server lets go at once; a holder that is merely slow keeps the
lock for as long as it is slow.

A **lease** is a promise with an expiry. MongoDB, Cosmos DB and Cassandra have no lock a session
holds, so the lock is a record that says who holds it until when, renewed in the background every
`RenewalInterval` while the handle is held. That buys the property the relational stores get for
free — **a holder that dies does not keep the lock forever**: the others wait about
`LeaseDuration` (up to a second and a fraction more on Cosmos DB and Cassandra) — and costs its
converse: **a live holder that cannot renew in time loses the lock**.

What a holder is promised, exactly:

- **The term.** A lease is held for `LeaseDuration` counted from the moment the write that took it,
  or the last renewal the store confirmed, was **sent** — never from when its answer arrived. The store
  applied that write later, so its own expiry comes later still.
- **`Lost` comes first.** The handle's `Lost` token is signalled a tenth of the lease before the term
  ends without a confirmed renewal, whatever the store or its driver is still doing (a renewal that
  hangs, a driver retrying on its own), and at once when a renewal finds the lease gone or taken. Each
  renewal is given until that moment and no longer. From then on the handle is not a holder: the lease
  is not renewed any more, and re-acquiring the name takes it afresh.
- **Another holder gets the lease only after that.** The store hands the lease over once its own expiry
  has passed, which is after the term, which is after `Lost`. This holds as long as the holder's
  process is not frozen for longer than that tenth of the lease (a long garbage collection, a stalled
  container or virtual machine) and the store's clock does not jump forward: those let the lease
  expire while the first holder has not yet been told, and are beyond what any lease can see.
- **Releasing never waits on a renewal.** A release (a disposed handle, an ended transaction, a disposed
  unit of work) stops the renewals and deletes the lease, waiting at most `LeaseDuration` for the store:
  by then the lease has run out on its own. A unit of work releases all its leases at once.

Work that can outlive a lease should observe `Lost`, and stop. Where two writers must never overlap
even across a frozen process, make the write itself conditional as well (a
[concurrency token](concurrency.md)): a lease narrows the window, it does not close it.

The relational stores never signal `Lost`.

## Tuning

```csharp
services.AddSingleton(new NamedLockOptions
{
    LeaseDuration = TimeSpan.FromSeconds(30),           // the default; at least one second
    RenewalInterval = TimeSpan.FromSeconds(10),         // default: a third of the lease
    PollInterval = TimeSpan.FromMilliseconds(100),      // the default
});
```

| Option | Default | What it bounds |
|---|---|---|
| `LeaseDuration` | 30 s | how long a dead holder keeps the others waiting (leases only): never less, and on Cosmos DB and Cassandra up to a second and a fraction more. A live holder is told it lost the lock (`Lost`) nine tenths of it after its last confirmed write was sent |
| `RenewalInterval` | a third of the lease | how often a held lease is renewed: a renewal can fail and be asked again before the lease is lost. It must be shorter than nine tenths of the lease |
| `PollInterval` | 100 ms | the longest pause between two attempts on PostgreSQL (ten times this for a lease, each of whose attempts is a write), the slice a server wait is cut into, and how soon a cancellation is noticed on the relational stores |

## Keys

- **PostgreSQL** locks one `bigint`: the first eight bytes of the SHA-256 digest of the name's UTF-8
  bytes, read big-endian as a signed integer. It is the same in every process, on every machine and
  in every version of this library. Collisions are possible in 64 bits, and harmless to correctness:
  two names that collide only wait for each other. The single-`bigint` key space is apart from the
  two-`int` one, so a lock taken by hand with `pg_advisory_xact_lock(ns, hashtext(id))` does **not**
  exclude one taken here — while two code bases share a database, both must take the lock the same way.
- **SQL Server** (255 characters), **MySQL and MariaDB** (64 characters) and **Cosmos DB** (an item id,
  255 characters) key on the name itself when it is short, printable ASCII with no space, and has
  none of the characters the store cannot keep apart: upper-case letters on MySQL and MariaDB (MySQL
  compares lock names ignoring case, which would make `Item:1` and `item:1` one lock), and `/`, `\`,
  `?` and `#` on Cosmos DB. Any other name is keyed as `~` and the hex SHA-256 of its bytes, cut to
  the store's length — and a name that itself starts with `~` is always hashed, so two names never
  share a key.
- **MongoDB** (`_id`) and **Cassandra** (the row key) key on the name as it is.

The scope follows the store: a PostgreSQL or SQL Server lock belongs to the database, a MongoDB,
Cosmos DB or Cassandra lease to the database or keyspace that holds `_locks`/`equantic_locks`, and a
MySQL or MariaDB lock to the **whole server** — two databases on one server share their lock names.

## What it costs

- Outside a transaction, a relational lock **pins a pooled connection** until it is released, so the
  session that holds it is never handed to anyone else. Many locks held at once need as many
  connections.
- A lease is a **write** to take, a write every `RenewalInterval` while held, and a write per attempt
  while waiting (plus a read and a clock query on Cosmos DB when the lease is held by someone else);
  a waiter backs off from a few milliseconds to ten `PollInterval`s between attempts.
- The lease stores are created on first use, as the sequences' `_sequences` are: the
  `_locks` collection and its TTL index on MongoDB, the `_locks` container on Cosmos DB (on a
  database without shared throughput it gets its own — create it yourself, partitioned by `/id`, to
  choose), and the `equantic_locks` table on Cassandra.

## Out of scope

Row-locking reads (`SELECT … FOR UPDATE`, `SKIP LOCKED`) are not named locks: Cosmos DB and
Cassandra have no row locks to map them to. A lock on `item:<id>` serialises everyone who asks for
`item:<id>`, and nothing else — a writer that does not ask is not stopped.
