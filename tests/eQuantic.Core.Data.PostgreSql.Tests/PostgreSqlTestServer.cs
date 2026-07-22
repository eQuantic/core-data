using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.PostgreSql.Extensions;
using eQuantic.Core.Data.PostgreSql.Repository;
using eQuantic.Core.Data.Repository;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace eQuantic.Core.Data.PostgreSql.Tests;

/// <summary>
///     Boots one PostgreSQL container for the whole test run and hands each test a fresh, isolated database
///     wired through the provider's own DI registrations. Skips gracefully when Docker is unavailable.
/// </summary>
[SetUpFixture]
public sealed class PostgreSqlTestServer
{
    private static PostgreSqlContainer? _container;
    private static Exception? _startupError;

    /// <summary>The admin connection string (the container's default database).</summary>
    public static string AdminConnectionString { get; private set; } = "";

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder().Build();
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
            Assert.Ignore($"PostgreSQL test container is unavailable (Docker required): {_startupError.Message}");
        }
    }

    /// <summary>Creates a fresh, isolated database with the test model registered.</summary>
    public static async Task<PostgreSqlTestDatabase> NewDatabaseAsync(Action<IServiceCollection>? configure = null)
    {
        var name = "db_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE {name}";
            await command.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = name };
        return new PostgreSqlTestDatabase(builder.ConnectionString, configure);
    }
}

/// <summary>Base class that skips a test when the container is unavailable, with shared helpers.</summary>
public abstract class PostgreSqlIntegrationTest
{
    [SetUp]
    public void RequireDocker() => PostgreSqlTestServer.EnsureAvailable();

    /// <summary>A fresh database with the schema migration already applied — ready for repository work.</summary>
    protected static async Task<PostgreSqlTestDatabase> NewSchemaAsync(Action<IServiceCollection>? configure = null)
    {
        var db = await PostgreSqlTestServer.NewDatabaseAsync(configure);
        await db.Resolve<IMigrationRunner>().RunAsync();
        return db;
    }

    protected static IAsyncRepository<SaleOrder, Guid> OrderRepo(PostgreSqlTestDatabase db) =>
        db.Resolve<IAsyncRepository<SaleOrder, Guid>>();

    protected static IAsyncRepository<Ticket, long> TicketRepo(PostgreSqlTestDatabase db) =>
        db.Resolve<IAsyncRepository<Ticket, long>>();

    protected static PostgreSqlDefaultUnitOfWork Uow(PostgreSqlTestDatabase db) =>
        db.Resolve<PostgreSqlDefaultUnitOfWork>();

    /// <summary>Stages the orders and flushes them through the unit of work.</summary>
    protected static async Task Seed(PostgreSqlTestDatabase db, params SaleOrder[] orders)
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
public sealed class PostgreSqlTestDatabase : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;

    public PostgreSqlTestDatabase(string connectionString, Action<IServiceCollection>? configure)
    {
        var services = new ServiceCollection();
        services.AddPostgreSqlDatabase(connectionString, TestSchema.Configure);
        services.AddPostgreSqlRepositories();
        services.AddPostgreSqlMigrations(typeof(TestSchema).Assembly);
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
