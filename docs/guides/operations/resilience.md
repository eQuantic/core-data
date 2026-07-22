# Resilience

Transient faults — failovers, connection blips, throttling — are a fact of managed databases. The
relational engine ships an opt-in retry with **honest semantics**; the NoSQL drivers largely bring
their own (documented below).

## Relational — `AddRelationalResilience`

```csharp
services.AddRelationalResilience(options =>
{
    options.RetryCommits = true;     // see the semantics note before enabling
});
```

The rules, exactly as implemented:

- **Reads retry automatically.** A read is idempotent by construction; a transient
  `DbException.IsTransient` fault retries with backoff. No opt-in needed.
- **Commits retry only behind `RetryCommits`.** A commit that failed *after the server may have
  applied it* is not idempotent — blind retry risks double-applying. Enabling `RetryCommits` is
  your statement that the flush is safe to repeat (idempotent upserts, or conflict detection via
  [concurrency tokens](../writing/concurrency.md) — with tokens, a duplicate retry surfaces as a
  conflict instead of a double write). The flush pipeline is re-runnable precisely for this.
- **Nothing retries inside an explicit transaction.** The transaction's state on the server is
  unknowable mid-flight; retrying inside would lie about atomicity. The failure propagates and the
  caller owns the transaction-level retry.

That is the same three-gate thinking applied to failures: reads are free, commit retry is an
explicit opt-in with stated preconditions, transactions refuse to pretend.

## The NoSQL providers

- **Cosmos DB** — the SDK retries throttling (429) and transient faults itself (configurable via
  `CosmosClientOptions`); the provider does not stack a second retry on top.
- **MongoDB** — the driver's retryable reads/writes apply (`retryWrites=true` by default on modern
  connection strings).
- **Cassandra** — the driver's retry/load-balancing policies govern; configure them on the cluster
  builder. Idempotence caveats for LWT writes mirror the relational commit note: a lost LWT
  response is a conflict on retry, which the token machinery reports correctly.

## Timeouts and cancellation

Every async API takes a `CancellationToken` and flows it into the driver call. Configure
store-level timeouts where they belong (connection strings, client options) — the engine does not
override them silently.
