# AotProbe

A NativeAOT smoke test for the PostgreSQL stack: a `PublishAot` console that compiles the engine to
a native single binary and runs a real round-trip (add → commit → filtered read → projection), plus
an offline `Explain()` that exercises model building, the filter interpreter and SQL rendering with
no I/O.

It is the reproducible evidence behind the documentation's
[Trimming and NativeAOT](https://equantic.github.io/core-data/guides/operations/aot.html) page —
and the place to re-verify AOT after engine changes.

```bash
docker run -d --name pg -e POSTGRES_USER=probe -e POSTGRES_PASSWORD=probe -e POSTGRES_DB=probe \
  -p 55432:5432 postgres:17-alpine

dotnet publish samples/AotProbe -c Release -r osx-arm64      # or linux-x64, win-x64

PG_CONN="Host=localhost;Port=55432;Database=probe;Username=probe;Password=probe" \
  ./samples/AotProbe/bin/Release/net10.0/osx-arm64/publish/AotProbe
```

Expected output:

```text
Explain (offline, no I/O):
  SELECT "id", "name", "category", "price" FROM "widgets" WHERE (...) ORDER BY "name" ASC
  accessor generated for Widget: True
Live round-trip: read 1 row(s), projected 1 row(s).
OK
```

Without `PG_CONN` it runs the offline path only (still a full AOT exercise of the query machinery).

The probe is deliberately plain application code — no `[DynamicDependency]`, no trimmer descriptor,
no ILLink XML. Everything AOT needs is a library concern now, and the probe shows the two
registration choices that make it so: `AddPostgreSqlRepository<Widget, Guid>()` (closed-generic
repositories plus the unit of work through an explicit factory) and
`AddPostgreSqlMigrations(source => source.Add<WidgetsSetup>())` (migrations named instead of
scanned). The bundled source generator supplies the reflection-free accessors on its own.
