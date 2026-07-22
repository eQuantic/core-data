# eQuantic.Core.Data

**One repository contract. Six stores. No lies.**

eQuantic.Core.Data is a multi-store data engine for .NET: the same `IRepository<TEntity, TKey>` /
`IUnitOfWork` contracts implemented **natively** on PostgreSQL, MySQL/MariaDB, SQL Server, MongoDB,
Azure Cosmos DB and Apache Cassandra — each on its official driver, none through Entity Framework.

```csharp
// The same code, on any of the six stores:
var overdue = await repository.GetPagedAsync(new PageRequest(1, 50),
    new QueryOptions<Invoice>()
        .Where(invoice => invoice.DueDate < today && invoice.Status == Status.Open)
        .OrderBy(invoice => invoice.DueDate));
```

## The philosophy: three gates, no lies

Every ORM eventually faces the same question: *what happens when the store cannot do what the code
asks?* Most answer by silently doing something else — evaluating in memory without telling you,
scanning without telling you, overwriting without telling you. This engine answers with three rules,
applied to every feature on every provider:

1. **Semantics are preserved.** A query returns what it says or it refuses. Nothing is "emulated"
   in a way that changes meaning.
2. **Cost is explicit and opt-in.** When honoring the semantics costs something the store didn't
   sign up for — a client-side residual filter, an `ALLOW FILTERING` scan, a Paxos round — the code
   opts in explicitly (`.AllowClientEvaluation()`, `.AllowFiltering()`, a declared
   `[ConcurrencyToken]`), or the call throws with guidance.
3. **Cost is observable.** `Explain()` — on queries *and* on models — reports every decision the
   engine made: what pushed down, what runs client-side, what an annotation ended up mapping to.
   The traces carry the facts no driver instrumentation knows.

A `NotSupportedException` here is not a shrug — it is a design decision with a message that tells
you what to do instead. And when a feature *can* be honored through a principled mechanism (LWT on
Cassandra, `$unionWith` on MongoDB, token-range predicates, OR query-splitting), it is.

## What you model once, every store honors

Entities are modeled with **one store-neutral vocabulary** — eQuantic-owned annotations and fluent
builders, never driver attributes — with a deterministic precedence:
`conventions < annotations < fluent`.

```csharp
[Entity("sale_orders")]
public sealed class SaleOrder : IEntity<Guid>
{
    [EntityKey]                        public Guid Code { get; set; }
    [StoredAs("client_name")]          public string Name { get; set; } = "";
    [ConcurrencyToken]                 public int Revision { get; set; }
    [PartitionKey]                     public string Region { get; set; } = "";
    [ClusteringKey(Descending = true)] public DateTime At { get; set; }
    [SearchIndex]                      public string Description { get; set; } = "";
}
```

`[ConcurrencyToken]` is a versioned `WHERE` on SQL, an `_etag` `If-Match` on Cosmos, a lightweight
transaction on Cassandra, a conditional replace on MongoDB. `[SearchIndex]` is a SASI index on
Cassandra and a GIN trigram index on PostgreSQL. Each store gets its **native mechanism**; the
entity never changes.

## Where to start

| | |
|---|---|
| **[Getting started](guides/getting-started/postgresql.md)** | Install, register, first entity, CRUD — per provider. |
| **[Concepts](guides/concepts/contracts.md)** | The contracts, the lean write model, specifications. |
| **[Modeling](guides/modeling/overview.md)** | Conventions, the annotation vocabulary, fluent builders, `Explain()`. |
| **[Querying](guides/querying/pushdown.md)** | Pushdown and the honest gates, paging, aggregates, GROUP BY, UNION. |
| **[Writing](guides/writing/transactions.md)** | Commits, transactions per store, optimistic concurrency, lifecycle. |
| **[Migrations](guides/migrations/index.md)** | Typed, fluent, model-driven schema — on all six stores. |
| **[Providers](guides/providers/postgresql.md)** | Per-store capability deep dives, with the *why* of every rejection. |
| **[Cookbook](guides/cookbook/multi-tenancy.md)** | Multi-tenancy, value objects, auditing, coming from EF. |
| **[API Reference](api/eQuantic.Core.Data.Repository.yml)** | Every public type, generated from the source docs. |

## Packages

| Package | What it is |
|---|---|
| `eQuantic.Core.Data` | The contracts, the modeling vocabulary, migrations, conventions. |
| `eQuantic.Core.Data.Relational` | The shared SQL engine (dialect-driven). |
| `eQuantic.Core.Data.PostgreSql` | PostgreSQL over Npgsql. |
| `eQuantic.Core.Data.MySql` | MySQL and MariaDB over MySqlConnector. |
| `eQuantic.Core.Data.SqlServer` | SQL Server over Microsoft.Data.SqlClient. |
| `eQuantic.Core.Data.MongoDb` | MongoDB over the official driver. |
| `eQuantic.Core.Data.CosmosDb` | Azure Cosmos DB over the official SDK. |
| `eQuantic.Core.Data.Cassandra` | Apache Cassandra over the DataStax driver. |
