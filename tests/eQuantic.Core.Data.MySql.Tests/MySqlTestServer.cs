using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.MySql.Extensions;
using eQuantic.Core.Data.MySql.Repository;
using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Repository;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Testcontainers.MySql;

namespace eQuantic.Core.Data.MySql.Tests;

/// <summary>An order entity for the MySQL tests (no collection members — MySQL has no array columns).</summary>
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

/// <summary>An identity-keyed entity: MySQL cannot read the generated key back, and the engine says so.</summary>
public sealed class Ticket : IEntity<long>
{
    public long Id { get; set; }

    public string Label { get; set; } = "";

    public long GetKey() => Id;

    public void SetKey(long key) => Id = key;
}

/// <summary>The relational mapping shared by the integration tests.</summary>
internal static class TestSchema
{
    public static void Configure(RelationalModelBuilder builder) => builder
        .Entity<SaleOrder>(entity => entity.Table("sale_orders"))
        .Entity<Ticket>(entity => entity.Key(x => x.Id, generated: true));
}

/// <summary>The schema migration the runner discovers.</summary>
[Migration("MySQL schema setup", 2026, 1, 1, 0, 0, 0)]
public sealed class SchemaSetupMigration : Data.Migration.Migration
{
    /// <inheritdoc />
    public override void Up(IMigrationBuilder migration) => migration
        .For<SaleOrder>(order => order
            .EnsureCollection()
            .Index(x => x.Customer))
        .For<Ticket>(ticket => ticket
            .EnsureCollection());
}

/// <summary>
///     Boots one MySQL container for the whole test run (root user, so per-test databases can be created) and
///     hands each test a fresh, isolated database wired through the provider's own DI registrations.
/// </summary>
[SetUpFixture]
public sealed class MySqlTestServer
{
    private static MySqlContainer? _container;
    private static Exception? _startupError;

    /// <summary>The admin (root) connection string.</summary>
    public static string AdminConnectionString { get; private set; } = "";

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        try
        {
            _container = new MySqlBuilder().WithUsername("root").Build();
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

    /// <summary>Skips the calling test when the container could not start (Docker unavailable).</summary>
    public static void EnsureAvailable()
    {
        if (_startupError is not null)
        {
            Assert.Ignore($"MySQL test container is unavailable (Docker required): {_startupError.Message}");
        }
    }

    /// <summary>Creates a fresh, isolated database with the test model registered.</summary>
    public static async Task<MySqlTestDatabase> NewDatabaseAsync(Action<IServiceCollection>? configure = null)
    {
        var name = "db_" + Guid.NewGuid().ToString("N");
        await using (var connection = new MySqlConnection(AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE {name}";
            await command.ExecuteNonQueryAsync();
        }

        var builder = new MySqlConnectionStringBuilder(AdminConnectionString) { Database = name };
        return new MySqlTestDatabase(builder.ConnectionString, configure);
    }
}

/// <summary>Base class that skips a test when the container is unavailable, with shared helpers.</summary>
public abstract class MySqlIntegrationTest
{
    [SetUp]
    public void RequireDocker() => MySqlTestServer.EnsureAvailable();

    /// <summary>A fresh database with the schema migration already applied — ready for repository work.</summary>
    protected static async Task<MySqlTestDatabase> NewSchemaAsync(Action<IServiceCollection>? configure = null)
    {
        var db = await MySqlTestServer.NewDatabaseAsync(configure);
        await db.Resolve<IMigrationRunner>().RunAsync();
        return db;
    }

    protected static IAsyncRepository<SaleOrder, Guid> OrderRepo(MySqlTestDatabase db) =>
        db.Resolve<IAsyncRepository<SaleOrder, Guid>>();

    protected static MySqlDefaultUnitOfWork Uow(MySqlTestDatabase db) =>
        db.Resolve<MySqlDefaultUnitOfWork>();

    /// <summary>Stages the orders and flushes them through the unit of work.</summary>
    protected static async Task Seed(MySqlTestDatabase db, params SaleOrder[] orders)
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
public sealed class MySqlTestDatabase : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;

    public MySqlTestDatabase(string connectionString, Action<IServiceCollection>? configure)
    {
        var services = new ServiceCollection();
        services.AddMySqlDatabase(connectionString, TestSchema.Configure);
        services.AddMySqlRepositories();
        services.AddMySqlMigrations(typeof(TestSchema).Assembly);
        configure?.Invoke(services);

        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
    }

    public T Resolve<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
    }
}
