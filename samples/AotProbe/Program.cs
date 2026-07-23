using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.PostgreSql.Extensions;
using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Read;
using Microsoft.Extensions.DependencyInjection;

// A NativeAOT smoke test for the PostgreSQL stack. It always runs the offline path — building the model
// and asking Explain() to render a real query plan, which exercises the reflection-heavy model/mapping/SQL
// machinery with no I/O. If PG_CONN is set it also does a live round-trip (add -> commit -> filtered read ->
// projection). A native binary that reaches "OK" proves the engine's query machinery runs under AOT.

var services = new ServiceCollection();
services.AddPostgreSqlDatabase(
    Environment.GetEnvironmentVariable("PG_CONN") ?? "Host=localhost;Database=probe;Username=probe;Password=probe",
    model => model.Entity<Widget>(entity => entity.Table("widgets")));
services.AddPostgreSqlRepository<Widget, Guid>();   // AOT-friendly: unit of work + closed-generic repos, no open generics
services.AddPostgreSqlMigrations(source => source.Add<WidgetsSetup>());   // explicit: no reflection, no rooting
var provider = services.BuildServiceProvider();

// Offline: build a plan. This runs model building, column mapping, the filter interpreter and SQL rendering.
using (var scope = provider.CreateScope())
{
    var repo = scope.ServiceProvider.GetRequiredService<IAsyncRepository<Widget, Guid>>();
    // A natural typed filter, decimal comparison included — the engine roots the money/date operators for AOT.
    var plan = ((IExplainableRepository<Widget>)repo).Explain(
        new QueryOptions<Widget>().Where(w => w.Category == "tools" && w.Price < 50m).OrderBy(w => w.Name));
    Console.WriteLine("Explain (offline, no I/O):");
    Console.WriteLine("  " + plan.Statement);
    Console.WriteLine($"  accessor generated for Widget: {EntityAccessors.For(typeof(Widget)) is not null}");
}

// Live: only when a connection string is provided.
if (Environment.GetEnvironmentVariable("PG_CONN") is { Length: > 0 })
{
    using var scope = provider.CreateScope();
    await scope.ServiceProvider.GetRequiredService<IMigrationRunner>().RunAsync();

    var repo = scope.ServiceProvider.GetRequiredService<IAsyncRepository<Widget, Guid>>();
    var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

    var widget = new Widget { Name = "Hammer", Category = "tools", Price = 19.90m };
    await repo.AddAsync(widget);
    await uow.CommitAsync();

    var found = await repo.GetFilteredAsync(w => w.Category == "tools",
        new QueryOptions<Widget>().OrderBy(w => w.Name));
    var names = await repo.GetMappedAsync(w => new WidgetRow(w.Id, w.Name, w.Price),
        new QueryOptions<Widget>().Where(w => w.Category == "tools"));

    Console.WriteLine($"Live round-trip: read {found.Count()} row(s), projected {names.Count()} row(s).");
}

Console.WriteLine("OK");

[eQuantic.Core.Data.Modeling.Entity("widgets")]
public sealed class Widget : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Price { get; set; }

    public Guid GetKey() => Id;
    public void SetKey(Guid key) => Id = key;
}

[Migration("Widgets setup", 2026, 7, 23, 0, 0, 0)]
public sealed class WidgetsSetup : Migration
{
    public override void Up(IMigrationBuilder migration) =>
        migration.For<Widget>(widget => widget.EnsureCollection().Index(x => x.Category));
}

public sealed record WidgetRow(Guid Id, string Name, decimal Price);

