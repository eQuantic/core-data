# Benchmarks

Measured, published, reproducible — including the numbers that flatter us and the ones that don't.
The suite lives in [`benchmarks/eQuantic.Core.Data.Benchmarks`](https://github.com/eQuantic/core-data/tree/master/benchmarks/eQuantic.Core.Data.Benchmarks)
and runs with one command:

```bash
dotnet run -c Release --project benchmarks/eQuantic.Core.Data.Benchmarks -- --filter "*Compare*" --job short
```

## Methodology

Four stacks, one PostgreSQL 17 (Testcontainers), one table (`bench_products`, 10 000 seeded rows,
indexed category), each stack set up the way its own documentation recommends:

- **raw Npgsql** — the baseline: hand-written SQL over a shared `NpgsqlDataSource`, manual
  materialization. The floor any abstraction pays against.
- **Dapper** — the same SQL through `Query*/Execute*` over the same data source.
- **EF Core** (Npgsql provider) — pooled `DbContextFactory`, `AsNoTracking` reads,
  `ExecuteUpdateAsync` for the set-based update.
- **eQuantic.Core.Data** — the PostgreSQL provider through DI, **a fresh scope per operation**
  (the way a request runs), repositories resolved per call.

BenchmarkDotNet `ShortRun` (1 launch, 3 warmup, 3 iterations) with `MemoryDiagnoser`. Environment:
Apple M4 Pro (arm64), .NET 10.0.9, macOS, PostgreSQL 17 in Docker on the same machine. Short runs
trade tight confidence intervals for practical runtime; treat single-digit-percent deltas as noise
and the shape of the table as the signal.

## Reads

| Scenario | raw Npgsql | Dapper | EF Core | eQuantic |
|---|---:|---:|---:|---:|
| Point read by key | 174.1 µs / 2.7 KB | 1.01× / 3.4 KB | 1.12× / 8.7 KB | **1.04×** / 8.9 KB |
| Filtered 500 rows (entities) | 416.0 µs / 97 KB | 1.01× / 152 KB | 1.15× / 199 KB | **1.02×** / 175 KB |
| Projection 500 rows (3 columns) | 371.0 µs / 62 KB | 1.02× / 93 KB | 1.06× / 166 KB | 1.29× / 165 KB |
| Offset page (count + 20 rows) | 486.4 µs / 8.4 KB | 1.11× / 10.8 KB | 1.15× / 29.7 KB | **1.02×** / 22.2 KB |

Reading it honestly:

- **Entity reads run at raw-driver speed.** Point reads, filtered sets and paging land within
  2–4% of hand-written Npgsql — on par with Dapper, ahead of EF Core in every read scenario —
  *while* going through DI scoping, the repository contract, expression interpretation and the
  pushdown pipeline. Translation costs single-digit microseconds
  (see [the translation table](#translation-microbenchmarks)); the database dominates.
- **Projection is our one slow read (1.29×).** `GetMappedAsync` materializes the narrowed columns
  into an entity shell and then applies the projection delegate — a double materialization the
  other stacks don't do. Known, measured, and the declared target of the planned source-generated
  materializers. Until then: for hot projection paths, the filtered-entities read (at 1.02×) plus
  an in-memory select is the faster shape.

## Writes

| Scenario | raw Npgsql | Dapper | EF Core | eQuantic |
|---|---:|---:|---:|---:|
| Insert 1 row + commit | 165.2 µs / 3.7 KB | 0.98× / 4.8 KB | 1.03× / 14.7 KB | 1.84× / 8.7 KB |
| Insert 100 rows, one commit | 1 468 µs / 175 KB | 9.58× (per-row) / 251 KB | 1.85× / 878 KB | **0.89×** / 456 KB |
| Set-based update (500 rows) | 661.7 µs / 2.6 KB | 0.98× / 3.1 KB | 1.01× / 10.1 KB | **0.95×** / 10.4 KB |

Reading it honestly:

- **The batch flush is the engine's home ground.** 100 staged inserts commit in one `DbBatch` at
  hand-written-batch speed (0.89× is within short-run noise of 1.0) — **2.1× faster than EF Core**
  and **10.7× faster** than Dapper's idiomatic per-row `ExecuteAsync(sql, list)` (that asymmetry
  is Dapper's usage pattern, not a rigged comparison — batching by hand in Dapper means writing
  the `DbBatch` yourself, which is the baseline column).
- **A single tiny insert pays the flush machinery (1.84×, ≈ +140 µs).** Scope creation, staging,
  lifecycle stamping and batch assembly are amortized brilliantly across a commit and visibly not
  across one 165 µs row. In a request that did anything else, 140 µs disappears; in a hot
  single-row-insert loop, stage several and commit once — that is the write model working as
  designed. A single-write fast path is on the improvement list.
- **Set-based updates are server-dominated** — all four stacks within ±5%. The typed
  `UpdateManyAsync` translation costs nothing measurable.
- **Allocations**: the engine allocates 2–3× raw Npgsql (scope + options + interpretation) —
  consistently **less than EF Core** on writes (8.7 vs 14.7 KB single; 456 vs 878 KB batch),
  more than Dapper everywhere. Driving this toward Dapper's numbers is the other declared goal of
  the source-generator work.

## Translation microbenchmarks

The recurring client-side cost the engine adds is translation (predicate → IR → provider plan) —
measured separately, no I/O ([`TranslationBenchmarks`](https://github.com/eQuantic/core-data/tree/master/benchmarks/eQuantic.Core.Data.Benchmarks)):

| Benchmark | Mean | Allocated |
|---|---:|---:|
| Interpret — simple equality | ~0.4 µs | 1.7 KB |
| Interpret — composite (3 clauses) | ~2.9 µs | 8.9 KB |
| Cassandra plan — fully pushed down | ~3.3 µs | 10.4 KB |
| Cassandra plan — with residual rebuild | ~4.3 µs | 7.9 KB |
| Cassandra plan — OR-split (2 branches) | ~7.6 µs | 13.9 KB |
| Update — set only | ~0.4 µs | 1.8 KB |

The most expensive translation the engine performs — an OR-split plan — costs single-digit
microseconds: noise against any network round trip, which is why the end-to-end tables above sit
at driver speed.

## Reproducing

```bash
git clone https://github.com/eQuantic/core-data && cd core-data
dotnet run -c Release --project benchmarks/eQuantic.Core.Data.Benchmarks -- --filter "*Compare*" --job short
```

Docker required (the run starts and disposes its own PostgreSQL container). Results land in
`BenchmarkDotNet.Artifacts/results/`. Numbers will differ on your hardware; the ratios are the
portable part.
