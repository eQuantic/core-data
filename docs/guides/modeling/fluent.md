# Fluent builders

Each provider's registration takes a model builder — the last word in the precedence chain and the
home of everything an attribute cannot express (lambdas, projections, explicit paths). One
`Entity<TEntity>(…)` call per mapped type.

## Relational — `RelationalModelBuilder`

```csharp
services.AddPostgreSqlDatabase(connectionString, model => model
    .Entity<SaleOrder>(entity => entity
        .Table("sale_orders")
        .Key(x => x.Code)                                  // or: .Key(x => x.Id, generated: true)
        .Key(x => new { x.OrderId, x.LineNo })             // composite key (see below)
        .Column(x => x.Name, "client_name")
        .Ignore(x => x.Scratch)
        .Facet(x => x.Name, length: 200)                   // varchar(200)
        .Facet(x => x.Total, precision: 18, scale: 2)      // numeric(18,2)
        .Converts(x => x.Email, email => email.Value, EmailAddress.Create)
        .ConcurrencyToken(x => x.Version)                  // int, long or Guid
        .Reference(x => x.Buyer, x => x.BuyerCode)         // FK the {Nav}Id convention can't guess
        .Collection(x => x.Items, item => item.OrderCode)  // element-side FK
        .SearchIndex(x => x.Description)                   // GIN trigram on PostgreSQL
        .ClusteringKey(x => x.PlacedAt, descending: true)));
```

**Composite keys.** `Key(x => new { x.OrderId, x.LineNo })` declares a two-column primary key
(member order = column order). Point lookups take the same shape as a tuple —
`GetAsync((orderId, lineNo))` — and updates/deletes address every key column. Two honest limits: a
composite key cannot be database-generated, and keyset paging / reference `Include` into a
composite-keyed target refuse with guidance (a single FK column cannot address a two-column key).

**Converters.** `Converts(selector, toStored, fromStored)` maps a domain type (a Value Object, an
enum-as-string) to a stored scalar. The conversion applies **everywhere**: DDL column type, insert
and update binding, filter constants, set-based updates and materialization — declared once.

## MongoDB — `MongoModelBuilder`

```csharp
services.AddMongoRepositories(connectionString, "shop", model => model
    .Entity<Product>(entity => entity
        .Collection("products")
        .Key(x => x.Code)                                  // the _id member
        .Field(x => x.Label, "l")                          // BSON element name
        .Ignore(x => x.Scratch)
        .Converts(x => x.Grade, g => g.ToString(), s => Enum.Parse<Grade>(s))
        .ConcurrencyToken(x => x.Version)
        .ClusteringKey(x => x.Category).ClusteringKey(x => x.Score, descending: true)
        .TimeToLive(x => x.CreatedAt, TimeSpan.FromHours(1))));
```

Renames and converters flow into the driver's class maps, so **filters and sorts render against
the stored representation** — the driver serializes filter constants through the member's
serializer. The model applies once per process (the driver's class maps are global and freeze on
first use); configure it at startup.

## Cosmos DB — `CosmosModelBuilder`

```csharp
services.AddCosmosDatabase(connectionString, "shop", model => model
    .Converts<OrderStatus, string>(                        // TYPE-level, by design (see below)
        status => status.ToString().ToLowerInvariant(),
        stored => Enum.Parse<OrderStatus>(stored, ignoreCase: true))
    .Entity<Order>(entity => entity
        .Container("orders")
        .Id(x => x.Code)
        .PartitionKey(x => x.Tenant).PartitionKey(x => x.Region)   // hierarchical (≤ 3 levels)
        .ClusteringKey(x => x.Kind).ClusteringKey(x => x.At, descending: true)
        .TimeToLive(TimeSpan.FromDays(30))
        .ConcurrencyToken(x => x.ETag)));                  // string member carrying _etag
```

**Why is Cosmos `Converts` type-level?** The SDK's LINQ translation serializes a filter's
*constants* by their type. A per-member converter could shape the document but not the constant in
`x => x.Status == OrderStatus.Open` — the comparison would silently miss. A type-level converter
keeps documents and filters converting identically; the asymmetry with the relational/Mongo
per-member `Converts` is deliberate and documented rather than papered over.

## Cassandra — `CassandraModelBuilder`

```csharp
services.AddCassandraRepositories(model => model
    .Entity<Reading>(entity => entity
        .Table("readings")
        .PartitionKey(x => x.TenantId).PartitionKey(x => x.SensorId)  // composite partition
        .ClusteringKey(x => x.At, descending: true)
        .Key(x => x.SensorId)                              // point-lookup column (defaults to first partition key)
        .Column(x => x.Value, "reading_value")
        .Counter(x => x.Hits)                              // counter table column
        .SearchIndex(x => x.Quality, CassandraSearchMode.Contains)
        .ConcurrencyToken(x => x.Version)                  // writes become LWTs
        .TimeToLive(TimeSpan.FromDays(7))));               // table default_time_to_live
```

Counter tables enforce Cassandra's own rule at model build: every non-key column must be a counter,
and counters only move through increments (`UpdateMany(filter, x => new Reading { Hits = x.Hits + 1 })`).

## Building without DI

`CosmosModelBuilder.Build()` and `MongoModelBuilder.Build()` are public for manual hosting — the
built model feeds `CosmosClientFactory.Create(connectionString, model)` and gives you
[`Explain()`](explain.md) anywhere.
