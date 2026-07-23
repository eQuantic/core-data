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
| Point read by key | 190.1 µs / 2.7 KB | 1.03× / 3.4 KB | 1.25× / 8.7 KB | **0.92×** / 8.9 KB |
| Filtered 500 rows (entities) | 454.0 µs / 97 KB | 1.05× / 152 KB | 1.13× / 198 KB | **1.04×** / 175 KB |
| Projection 500 rows (3 columns) | 438.2 µs / 62 KB | 0.90× / 93 KB | 1.14× / 166 KB | **0.92×** / 130 KB |
| Offset page (count + 20 rows) | 572.6 µs / 8.4 KB | 1.07× / 10.8 KB | 1.07× / 29.4 KB | **0.94×** / 22.3 KB |

Reading it honestly:

- **Every read runs at raw-driver speed** — statistically indistinguishable from hand-written
  Npgsql (the sub-1.0 ratios are ShortRun noise, not magic), ahead of EF Core in all four
  scenarios, tied with Dapper — *while* going through DI scoping, the repository contract,
  expression interpretation and the pushdown pipeline. Translation costs single-digit microseconds
  (see [the translation table](#translation-microbenchmarks)); the database dominates.
- **Projection was our one slow read, and the first run of this suite caught it.** The original
  `GetMappedAsync` materialized entity shells and then applied the map — 1.29× and the worst line
  of the first published table. Two engine changes later it ties the floor: the common map shapes
  (constructor projections, member inits, single members) compile into **reader-direct
  projectors** — cached constructor invocation, no per-query expression compilation — and the
  projectors themselves are **cached by the map's structure** (the structural
  `ExpressionEqualityComparer` from eQuantic.Linq.Expressions), since C# rebuilds the expression
  tree on every call even for a lexically identical lambda. Reader-direct maps are closure-free by
  construction, so a cached projector can never smuggle a stale captured value; shapes the
  projector cannot prove fall back to the entity path, with identical results.

## Writes

| Scenario | raw Npgsql | Dapper | EF Core | eQuantic |
|---|---:|---:|---:|---:|
| Insert 1 row + commit | 165.1 µs / 3.7 KB | 1.02× / 4.8 KB | 1.05× / 14.6 KB | **1.00×** / 8.3 KB |
| Insert 100 rows, one commit | 1 496 µs / 175 KB | 9.86× (per-row) / 251 KB | 1.68× / 878 KB | **0.90×** / 456 KB |
| Set-based update (500 rows) | 627.1 µs / 2.6 KB | 1.00× / 3.1 KB | 1.10× / 10.1 KB | 1.13× / 10.4 KB |

Reading it honestly:

- **A single insert now runs at raw speed (1.00×)** — the fastest of the four stacks in this run.
  The first published table had it at 1.84×: the flush wrapped even a one-statement commit in an
  explicit transaction, paying `BEGIN`/`COMMIT` round trips a single atomic statement never
  needed. The engine now skips the local transaction for one-statement flushes — the same
  optimization EF Core applies, with identical all-or-nothing semantics.
- **The batch flush is the engine's home ground.** 100 staged inserts commit in one `DbBatch` at
  hand-written-batch speed (0.90×, within short-run noise of 1.0) — **1.9× faster than EF Core**
  and **11× faster** than Dapper's idiomatic per-row `ExecuteAsync(sql, list)` (that asymmetry is
  Dapper's usage pattern, not a rigged comparison — batching by hand in Dapper means writing the
  `DbBatch` yourself, which is the baseline column).
- **Set-based updates are server-dominated.** Across runs this scenario oscillates between 0.95×
  and 1.13× (ShortRun jitter on a ~0.6 ms server-bound statement); the typed `UpdateManyAsync`
  translation itself costs single-digit microseconds.
- **Allocations**: the engine allocates 2–3× raw Npgsql (scope + options + interpretation) —
  consistently **less than EF Core** (8.3 vs 14.6 KB single insert; 456 vs 878 KB batch; 129 vs
  165 KB projection), more than Dapper everywhere. Driving this toward Dapper's numbers is a
  declared goal of the planned source-generator work.

> These two engine optimizations exist *because* this suite ran: the first published table named
> projection (1.29×) and single-insert (1.84×) as the weak spots, both were fixed at the engine
> level, and the numbers above are the re-measurement. That loop — measure, publish the losses,
> fix, re-measure — is the point of keeping benchmarks in the repository.

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
