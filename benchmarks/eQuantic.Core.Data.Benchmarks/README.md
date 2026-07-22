# eQuantic.Core.Data — benchmarks

Two suites, one project:

## 1. Comparative (`Compare*`) — vs EF Core, Dapper and raw Npgsql

End-to-end scenarios against a real PostgreSQL 17 (Testcontainers — the run starts and disposes
its own container): point read, filtered set, projection, offset page, single insert, 100-row
commit, set-based update. Four stacks over the same table, each set up the way its own
documentation recommends (EF Core: pooled factory + `AsNoTracking`; eQuantic: DI scope per
operation, like a request).

```bash
dotnet run -c Release -- --filter "*Compare*" --job short
```

Published results, methodology and the honest reading (including the scenarios where we lose) live
in the documentation: **Operations → Benchmarks**
(`docs/guides/operations/benchmarks.md` / https://equantic.github.io/core-data/).

## 2. Translation (`*Translation*`) — the engine's client-side layer

The recurring client-side cost the engine adds to a query is **translation**: predicate → node
model → dialect-agnostic IR → provider plan. Everything after that is prepared/bound driver work.
These benchmarks measure exactly that layer — no containers, no I/O.

```bash
dotnet run -c Release -- --filter "*Translation*" --job short
```

### Reference numbers

Apple silicon (arm64), .NET 10, `--job short`, after v5.6's interpreted fold of parameter-free
operands (one-shot values are evaluated with `Compile(preferInterpretation: true)` — 10–14× cheaper
than paying the JIT per translation):

| Benchmark | Mean | Allocated |
|---|---:|---:|
| Interpret — simple equality | ~0.4 µs | 1.7 KB |
| Interpret — composite (3 clauses, inline `new DateTime`) | ~2.9 µs | 8.9 KB |
| Cassandra plan — fully pushed down | ~3.3 µs | 10.4 KB |
| Cassandra plan — with residual rebuild | ~4.3 µs | 7.9 KB |
| Cassandra plan — OR-split (2 native branches) | ~7.6 µs | 13.9 KB |
| Update — set only | ~0.4 µs | 1.8 KB |
| Update — increment | ~0.6 µs | 2.6 KB |
| Update — collection add | ~1.8 µs | 3.4 KB |

Reading: the most expensive translation the engine performs (an OR-split plan) costs single-digit
microseconds — noise against any network round-trip — and statements themselves are prepared once
per session.
