# Observability

Four complementary surfaces: **logs** for the queries as they run, **metrics** for the dashboards,
**traces** for the distributed picture, **Explain** for what will happen before you run it. All of
them carry the facts only the engine knows.

## Logs — `Microsoft.Extensions.Logging`, the way EF does it

The engine logs through the standard logging abstractions with **stable categories and event ids**
— no logger-specific packages, because none are needed: Serilog, NLog and the console all plug in
through the MEL providers they already ship. The ritual is the one you know from EF:

```csharp
// Serilog:
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("eQuantic.Core.Data", LogEventLevel.Information)   // all providers
    // or per provider: "eQuantic.Core.Data.postgresql.Command"
    .WriteTo.Console()
    .CreateLogger();
```

Categories are `eQuantic.Core.Data.{provider}.Command` (`postgresql`, `mysql`, `mariadb`,
`sqlserver`, `cassandra`, `mongodb`) and `eQuantic.Core.Data.cosmosdb.Request`. The events:

| Event | Id | Level | Carries |
|---|---|---|---|
| `CommandExecuted` | 10001 | Information | statement (placeholders), elapsed, rows where known; Cosmos adds status and the **RU charge** |
| `CommandFailed` | 10002 | Error | statement + the exception |
| `CommitExecuted` | 10101 | Information | staged writes flushed, elapsed |
| `ClientEvaluation` | 10201 | **Warning** | the residual that ran client-side (behind its opt-in) |
| `AllowFiltering` | 10202 | **Warning** | a Cassandra query running as a declared scan |
| `QuerySplit` | 10203 | **Warning** | an OR filter fanned out into parallel native queries |
| `ConcurrencyConflict` | 10301 | **Warning** | expected vs affected on a lost race |

The gates logging at Warning is deliberate: an opt-in that quietly became a hot path's habit
should surface in production logs, not in an incident review.

**Parameter values never log by default** — statements carry placeholders, the same policy the
traces follow. Turn values on the way you turn EF's on: deliberately, per environment —

```csharp
services.AddSingleton(new DataConventions { EnableSensitiveDataLogging = true });
```

(On MongoDB this also gates command *bodies*, which inherently carry values; on Cosmos DB request
bodies never log.)

## Metrics — `AddMeter("eQuantic.Core.Data")`

```csharp
services.AddOpenTelemetry().WithMetrics(metrics => metrics
    .AddMeter("eQuantic.Core.Data")
    .AddOtlpExporter());
```

| Instrument | Type | Meaning |
|---|---|---|
| `equantic.commands` / `equantic.command.failures` | counter | commands executed / failed (tag `db.system`) |
| `equantic.command.duration` | histogram (ms) | command latency |
| `equantic.commits` / `equantic.writes` | counter | flushes and the staged writes they carried |
| `equantic.client_evaluations` | counter | queries whose residual ran client-side |
| `equantic.allow_filtering` | counter | declared scans (Cassandra) |
| `equantic.query_splits` | counter | OR fan-outs |
| `equantic.concurrency_conflicts` | counter | lost optimistic-concurrency races |

The gate counters make the engine's honesty **graphable** — a rising `client_evaluations` on a
dashboard is an alert, not an archaeology project.

## Traces — `eQuantic.Core.Data`

Every provider emits `System.Diagnostics.Activity` spans on the shared source. Subscribe with any
OpenTelemetry setup:

```csharp
services.AddOpenTelemetry().WithTracing(tracing => tracing
    .AddSource("eQuantic.Core.Data")
    .AddOtlpExporter());
```

Spans follow OTel database conventions (`db.system`, `db.statement` with placeholders — **never
parameter values**) and add the engine's own tags, the ones no driver instrumentation can know:

| Tag | Meaning |
|---|---|
| `equantic.client_evaluation` | a residual filter ran in memory on this query |
| `equantic.split_queries` | the OR-split branch count (Cassandra) |
| `equantic.partition_scoped` | the fetch was pinned to a partition |
| `equantic.allow_filtering` | the query ran with a declared scan (Cassandra) |
| `equantic.writes` | staged writes flushed by this commit |

These make the gates *operable*: alert on `client_evaluation` spans in a hot path, graph
`partition_scoped` ratios on Cosmos, catch an accidental `allow_filtering` in production.

## Explain — before you run

Every repository implements `IExplainableRepository<TEntity>`; every model has `Explain()`:

```csharp
Console.WriteLine(repo.Explain(options));   // the plan: statement, residual, gates, notes
Console.WriteLine(model.Explain(dialect));  // the mapping: names, types, keys, tokens, indexes
```

Treat both as testable artifacts — pin the lines you rely on:

```csharp
var plan = repo.Explain(options);
Assert.That(plan.ClientEvaluation, Is.False, "this query must stay fully server-side");
Assert.That(plan.PartitionScoped, Is.True, "this query must pin the partition");
```

That turns performance regressions (a refactor that un-pins a partition, a filter that silently
grew a residual) into failing tests instead of production incidents.

## Query tags

`options.WithTag("catalog-page")` stamps the query — the tag lands where the store shows it (an
SQL comment, trace tags), connecting a slow statement in the store's own tooling back to the call
site that issued it.
