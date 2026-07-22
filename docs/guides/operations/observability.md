# Observability

Two complementary surfaces: **traces** for what happened in production, **Explain** for what will
happen before you run it. Both carry the facts only the engine knows.

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
