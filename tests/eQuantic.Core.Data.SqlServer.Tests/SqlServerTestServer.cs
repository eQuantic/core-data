using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.SqlServer.Extensions;
using eQuantic.Core.Data.SqlServer.Repository;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace eQuantic.Core.Data.SqlServer.Tests;

/// <summary>An order entity for the SQL Server tests (no collection members — no array columns).</summary>
public sealed class SaleOrder : IEntity<Guid>
{
    public Guid Id { get; set; }

    public string Customer { get; set; } = "";

    public string? Status { get; set; }

    public decimal Total { get; set; }

    public int Quantity { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>An identity-keyed entity: the database generates the key and OUTPUT INSERTED reads it back.</summary>
public sealed class Ticket : IEntity<long>
{
    public long Id { get; set; }

    public string Label { get; set; } = "";

    public long GetKey() => Id;

    public void SetKey(long key) => Id = key;
}

/// <summary>The relational mapping shared by the integration tests.</summary>
/// <summary>A run's state, stored as its labels by the annotation on the enum.</summary>
[Modeling.EnumType]
public enum RunState
{
    Queued,
    [Modeling.StoredAs("in_progress")]
    InProgress,
    Completed,
}

/// <summary>A delivery channel, stored as its labels by the model builder's <c>Enum&lt;Channel&gt;()</c>.</summary>
public enum Channel
{
    Email,
    Sms,
}

/// <summary>A weekday, kept as a plain enum: stored as its number.</summary>
public enum Weekday
{
    Monday = 1,
    Tuesday = 2,
}

/// <summary>Enums stored as labels, JSON members, and members the store fills, beside a client key.</summary>
public sealed class RunRecord : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public RunState State { get; set; }

    public RunState? Previous { get; set; }

    public Channel Channel { get; set; }

    public Weekday Day { get; set; }

    public System.Text.Json.JsonDocument? Payload { get; set; }

    public System.Text.Json.Nodes.JsonNode? Steps { get; set; }

    public System.Text.Json.JsonElement? Meta { get; set; }

    [Modeling.StoreGenerated]
    public long Sequence { get; set; }

    [Modeling.StoreGenerated]
    public DateTime RecordedAt { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A generated key read back with the store-generated members after it (SQL Server: OUTPUT INSERTED).</summary>
public sealed class Receipt : IEntity<long>
{
    public long Id { get; set; }

    public string Note { get; set; } = "";

    public long Number { get; set; }

    public DateTime IssuedAt { get; set; }

    public long GetKey() => Id;

    public void SetKey(long key) => Id = key;
}

internal static class TestSchema
{
    public static void Configure(RelationalModelBuilder builder) => WriteOutcomeSchema.Configure(builder)
        .Entity<SaleOrder>(entity => entity.Table("sale_orders"))
        .Entity<Ticket>(entity => entity.Key(x => x.Id, generated: true))
        .Enum<Channel>()
        .Entity<RunRecord>(_ => { })
        .Entity<Receipt>(entity => entity
            .Key(x => x.Id, generated: true)
            .Generated(x => x.Number)
            .Generated(x => x.IssuedAt, Modeling.StoreValue.Clock));
}

/// <summary>The schema migration the runner discovers.</summary>
[Migration("SQL Server schema setup", 2026, 1, 1, 0, 0, 0)]
public sealed class SchemaSetupMigration : Data.Migration.Migration
{
    /// <inheritdoc />
    public override void Up(IMigrationBuilder migration) => migration
        .For<SaleOrder>(order => order
            .EnsureCollection()
            .Index(x => x.Customer))
        .For<Ticket>(ticket => ticket
            .EnsureCollection())
        .For<RunRecord>(run => run
            .EnsureCollection())
        .For<Receipt>(receipt => receipt
            .EnsureCollection());
}

/// <summary>
///     Boots one SQL Server container for the whole test run and hands each test a fresh, isolated database
///     wired through the provider's own DI registrations. Skips gracefully when Docker (or the amd64 image on
///     this host) is unavailable.
/// </summary>
[SetUpFixture]
public sealed class SqlServerTestServer
{
    private static MsSqlContainer? _container;
    private static Exception? _startupError;

    /// <summary>The admin (sa) connection string.</summary>
    public static string AdminConnectionString { get; private set; } = "";

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        try
        {
            _container = new MsSqlBuilder().Build();
            await _container.StartAsync();
            AdminConnectionString = _container.GetConnectionString();
        }
        catch (Exception ex)
        {
            _startupError = ex;
        }
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Skips the calling test when the container could not start.</summary>
    public static void EnsureAvailable()
    {
        if (_startupError is not null)
        {
            Assert.Ignore($"SQL Server test container is unavailable (Docker required): {_startupError.Message}");
        }
    }

    /// <summary>Creates a fresh, isolated database with the test model registered.</summary>
    public static async Task<SqlServerTestDatabase> NewDatabaseAsync(Action<IServiceCollection>? configure = null)
    {
        var name = "db_" + Guid.NewGuid().ToString("N");
        await using (var connection = new SqlConnection(AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE {name}";
            await command.ExecuteNonQueryAsync();
        }

        var builder = new SqlConnectionStringBuilder(AdminConnectionString) { InitialCatalog = name };
        return new SqlServerTestDatabase(builder.ConnectionString, configure);
    }
}

/// <summary>Base class that skips a test when the container is unavailable, with shared helpers.</summary>
public abstract class SqlServerIntegrationTest
{
    [SetUp]
    public void RequireDocker() => SqlServerTestServer.EnsureAvailable();

    /// <summary>A fresh database with the schema migration already applied — ready for repository work.</summary>
    protected static async Task<SqlServerTestDatabase> NewSchemaAsync(Action<IServiceCollection>? configure = null)
    {
        var db = await SqlServerTestServer.NewDatabaseAsync(configure);
        await db.Resolve<IMigrationRunner>().RunAsync();
        return db;
    }

    protected static IAsyncRepository<SaleOrder, Guid> OrderRepo(SqlServerTestDatabase db) =>
        db.Resolve<IAsyncRepository<SaleOrder, Guid>>();

    protected static IAsyncRepository<Ticket, long> TicketRepo(SqlServerTestDatabase db) =>
        db.Resolve<IAsyncRepository<Ticket, long>>();

    protected static SqlServerDefaultUnitOfWork Uow(SqlServerTestDatabase db) =>
        db.Resolve<SqlServerDefaultUnitOfWork>();

    /// <summary>Stages the orders and flushes them through the unit of work.</summary>
    protected static async Task Seed(SqlServerTestDatabase db, params SaleOrder[] orders)
    {
        var repo = OrderRepo(db);
        foreach (var order in orders)
        {
            await repo.AddAsync(order);
        }

        await Uow(db).CommitAsync();
    }
}

/// <summary>A single test's database, with the provider's services registered and a resolution scope open.</summary>
public sealed class SqlServerTestDatabase : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;

    public SqlServerTestDatabase(string connectionString, Action<IServiceCollection>? configure)
    {
        var services = new ServiceCollection();
        services.AddSqlServerDatabase(connectionString, model => TestSchema.Configure(ReadShapesSchema.Configure(model)));
        services.AddSqlServerRepositories();
        services.AddSqlServerMigrations(typeof(TestSchema).Assembly);
        configure?.Invoke(services);

        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
    }

    public T Resolve<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    /// <summary>A scope of its own — its own unit of work — over the same database.</summary>
    public IServiceScope NewScope() => _provider.CreateScope();

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
    }
}
