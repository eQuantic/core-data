# Getting started — Apache Cassandra

The Cassandra provider implements the eQuantic.Core.Data contracts directly on the
[DataStax C# driver](https://docs.datastax.com/en/developer/csharp-driver/) — with the access
pattern declared in the model, prepared statements cached per session, and the strictest version of
the engine's honesty rules: Cassandra only queries efficiently by its keys, and this provider never
pretends otherwise.

## 1. Install

```bash
dotnet add package eQuantic.Core.Data.Cassandra
```

## 2. Model the access pattern

In Cassandra you don't model entities — you model **queries**. The partition key decides where rows
live; clustering keys decide their order inside the partition. The model declares both:

```csharp
using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.Repository;

[Entity("readings")]
public sealed class Reading : IEntity<int>
{
    [PartitionKey]                     public int SensorId { get; set; }
    [ClusteringKey(Descending = true)] public DateTime At { get; set; }
    public double Value { get; set; }
    [SearchIndex]                      public string Quality { get; set; } = "";

    public int GetKey() => SensorId;
    public void SetKey(int key) => SensorId = key;
}
```

## 3. Register

```csharp
services.AddCassandraSession("shop", contactPoints: ["localhost"], port: 9042);
services.AddCassandraRepositories(model => model
    .Entity<Reading>(_ => { }));          // the annotations declared everything
services.AddCassandraMigrations(typeof(Program).Assembly);
```

## 4. Schema and usage

```csharp
[Migration("Readings setup", 2026, 7, 22, 12, 0, 0)]
public sealed class ReadingsSetup : Migration
{
    public override void Up(IMigrationBuilder migration) => migration
        .For<Reading>(reading => reading.EnsureCollection());
        // CREATE TABLE with PRIMARY KEY ((sensor_id), at) WITH CLUSTERING ORDER BY (at DESC)
        // + the SASI index for [SearchIndex]
}
```

```csharp
await repository.AddAsync(new Reading { SensorId = 7, At = DateTime.UtcNow, Value = 21.5 });
await unitOfWork.CommitAsync();          // prepared once, bound per write, executed concurrently

// partition-scoped read: efficient, no opt-in needed
var recent = await repository.GetFilteredAsync(
    r => r.SensorId == 7 && r.At > since,
    new QueryOptions<Reading>().OrderByDescending(r => r.At));
```

## The gates you will meet first

Cassandra is where the engine's honesty is most visible. Two opt-ins exist, and the exception
messages tell you which one you need:

```csharp
// filtering on a non-key column = a server-side scan; say so explicitly:
var poor = await repository.GetFilteredAsync(
    r => r.Value < 10,
    new QueryOptions<Reading>().AllowFiltering());

// a shape CQL cannot express (OR across columns, !=) = fetch + client-side residual; say so:
var odd = await repository.GetFilteredAsync(
    r => r.SensorId == 7 && (r.Value < 10 || r.Quality == "bad"),
    new QueryOptions<Reading>().AllowClientEvaluation());
```

Nothing scans and nothing runs client-side without those calls — and `Explain()` shows exactly what
each query will do before you run it. That is the [pushdown contract](../querying/pushdown.md).

## Where next

- [Cassandra deep dive](../providers/cassandra.md) — token ranges, OR query-splitting, counters,
  LWT concurrency, SASI search, TTL, consistency levels.
- [Modeling](../modeling/overview.md) · [Migrations](../migrations/index.md)
