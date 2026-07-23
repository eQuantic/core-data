# Trimming and NativeAOT

The honest, measured status — not a badge. The PostgreSQL stack **compiles to a native binary and
runs a real round-trip** (this is verified by [`samples/AotProbe`](https://github.com/eQuantic/core-data/tree/master/samples/AotProbe),
a `PublishAot` console that does add → commit → filtered read → projection against a live database).
Getting there surfaced exactly which walls exist and how to clear them; this page is that map.

## What runs under NativeAOT today

A native `AotProbe` (12 MB, no .NET runtime) produces:

```text
Explain (offline, no I/O):
  SELECT "id", "name", "category", "price" FROM "widgets" WHERE ("category" = @p0 AND "price" < @p1) ORDER BY "name" ASC
  accessor generated for Widget: True
Live round-trip: read 1 row(s), projected 1 row(s).
OK
```

So the engine's core runs natively: model building, the filter interpreter, SQL rendering
(including a **decimal comparison**), the **source-generated accessors**, DI resolution, and the
Npgsql round-trip. What made it work is below.

## The three things you do

### 1. Register repositories closed-generic

The open-generic registration (`AddPostgreSqlRepositories()`) asks the DI container to close
`RelationalRepository<,>` at runtime — which NativeAOT cannot do when the key is a value type
(`Guid`, `int`). Register per entity instead:

```csharp
services.AddPostgreSqlDatabase(connectionString, model => model.Entity<Widget>(...));
services.AddPostgreSqlRepository<Widget, Guid>();   // unit of work + closed-generic repos, no open generics
services.AddPostgreSqlRepository<Order, long>();
```

`AddPostgreSqlRepository<TEntity, TKey>()` names the closed type at the call site (so the AOT
compiler roots it) and registers the unit of work through an explicit factory (no reflection over
its constructor). Both are the AOT-safe forms of what the open-generic call does. The same shape
exists on every relational provider (`AddRelationalRepository<TEntity, TKey>`,
`Add{Provider}Repository<TEntity, TKey>`).

### 2. Ship the source generator (you already do)

The generator that emits reflection-free entity accessors is bundled in the package — no action
needed. Under AOT it is what materializes rows and reads members without reflection. The probe
confirms it: `accessor generated for Widget: True`.

### 3. Register migrations explicitly

Assembly scanning for `[Migration]` types is reflection: the trimmer removes the constructors of
types reachable only that way, so a scan finds nothing under AOT. Name them instead:

```csharp
services.AddPostgreSqlMigrations(source => source
    .Add<ProductsSetup>()
    .Add<ProductsBackfill>());
```

`source.Add<T>()` constructs with a plain `new` — statically rooted, no reflection, and faster
startup (no scan). Ordering still comes from each migration's `[Migration]` timestamp, so
registration order does not matter, and `Add(instance)` takes a migration with constructor
arguments. The same overload exists on every provider
(`AddMongoMigrations`, `AddCosmosMigrations`, `AddCassandraMigrations`). The scanning overload is
unchanged for JIT apps, and honours an explicitly registered source too.

## What the engine handles for you

- **Money and date comparisons.** A filter like `x => x.Price < 50m` is realized through
  `Expression.MakeBinary(LessThan, …)`, which reflects for `decimal.op_LessThan` at runtime — an
  operator the trimmer would otherwise remove. The package roots the operators of the common
  filterable value types (`decimal`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `DateOnly`,
  `TimeOnly`) via a module initializer, so decimal/date filters just work under AOT. Comparisons on
  primitives (`int`, `long`) use IL opcodes and need nothing.

## Known limits (the honest part)

- **`Expression.Compile()` falls back to the interpreter** under AOT (residual client-side filters,
  key selectors). It works, but interpreted — a reason to keep filters pushing down server-side
  (which is the [pushdown contract](../querying/pushdown.md) anyway).
- **jsonb document columns** serialize through reflection-based `System.Text.Json`; a model that
  maps dictionary/document members needs a JSON source-gen context for clean AOT. Scalar and
  converted columns are fine.
- **Wire-format filters** (`ExpressionSerializer` from eQuantic.Linq) are explicitly
  `[RequiresDynamicCode]` upstream — the serialized-filter feature is not AOT-compatible. Typed and
  string-path filters authored in-process are.
- **Trim analyzer warnings remain** in the model-building and projection paths (generic-parameter
  `[DynamicallyAccessedMembers]` propagation). They are warnings, not failures — the probe runs —
  and flowing the annotations through is incremental work in progress. This page will track it
  honestly rather than claim a clean bill early.

## Reproducing

```bash
docker run -d --name pg -e POSTGRES_USER=probe -e POSTGRES_PASSWORD=probe -e POSTGRES_DB=probe -p 55432:5432 postgres:17-alpine
dotnet publish samples/AotProbe -c Release -r <rid>       # e.g. osx-arm64, linux-x64
PG_CONN="Host=localhost;Port=55432;Database=probe;Username=probe;Password=probe" \
  ./samples/AotProbe/bin/Release/net10.0/<rid>/publish/AotProbe
```
