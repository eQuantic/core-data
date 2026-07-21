using System.Reflection;
using eQuantic.Core.Data.Cassandra.Extensions;
using eQuantic.Core.Data.Cassandra.Repository;
using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Repository;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.Cassandra;
using global::Cassandra;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>
///     Boots one real Apache Cassandra container for the whole test run (multi-arch image — provable locally on
///     arm64) and hands each test a fresh, isolated keyspace wired through the provider's own DI registrations.
///     Skips gracefully when Docker is not available.
/// </summary>
[SetUpFixture]
public sealed class CassandraTestServer
{
    private static CassandraContainer? _container;
    private static Exception? _startupError;

    /// <summary>The host the cluster is reachable on.</summary>
    public static string Host { get; private set; } = "";

    /// <summary>The mapped native-protocol port.</summary>
    public static int Port { get; private set; }

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        try
        {
            _container = new CassandraBuilder().WithImage("cassandra:4.1").Build();
            await _container.StartAsync();
            Host = _container.Hostname;
            Port = _container.GetMappedPublicPort(9042);
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
            Assert.Ignore($"Cassandra test container is unavailable (Docker required): {_startupError.Message}");
        }
    }

    /// <summary>Creates a fresh, isolated keyspace with the test model (and optional migrations) registered.</summary>
    public static CassandraTestDatabase NewDatabase(params Assembly[] migrationAssemblies) =>
        new(Host, Port, "ks_" + Guid.NewGuid().ToString("N"), TestSchema.Configure, migrationAssemblies, null);

    /// <summary>Creates a fresh keyspace with extra registrations (e.g. global query filters).</summary>
    public static CassandraTestDatabase NewDatabase(Action<IServiceCollection> configure, params Assembly[] migrationAssemblies) =>
        new(Host, Port, "ks_" + Guid.NewGuid().ToString("N"), TestSchema.Configure, migrationAssemblies, configure);
}

/// <summary>Base class that skips a test when the Cassandra container is unavailable, with shared helpers.</summary>
public abstract class CassandraIntegrationTest
{
    [SetUp]
    public void RequireDocker() => CassandraTestServer.EnsureAvailable();

    /// <summary>A fresh keyspace with the test model registered but no schema yet (migration tests drive the runner).</summary>
    protected static CassandraTestDatabase NewDatabase(params Assembly[] migrationAssemblies) =>
        CassandraTestServer.NewDatabase(migrationAssemblies);

    /// <summary>A fresh keyspace with the schema applied and extra registrations (e.g. global query filters).</summary>
    protected static async Task<CassandraTestDatabase> NewSchemaAsync(Action<IServiceCollection> configure)
    {
        var db = CassandraTestServer.NewDatabase(configure, typeof(SchemaSetupMigration).Assembly);
        await db.Resolve<IMigrationRunner>().RunAsync();
        return db;
    }

    /// <summary>A fresh keyspace with the schema migration already applied — ready for repository work.</summary>
    protected static async Task<CassandraTestDatabase> NewSchemaAsync()
    {
        var db = NewDatabase(typeof(SchemaSetupMigration).Assembly);
        await db.Resolve<IMigrationRunner>().RunAsync();
        return db;
    }

    protected static IAsyncRepository<Account, Guid> AccountRepo(CassandraTestDatabase db) =>
        db.Resolve<IAsyncRepository<Account, Guid>>();

    protected static IAsyncRepository<Reading, int> ReadingRepo(CassandraTestDatabase db) =>
        db.Resolve<IAsyncRepository<Reading, int>>();

    protected static CassandraDefaultUnitOfWork Uow(CassandraTestDatabase db) =>
        db.Resolve<CassandraDefaultUnitOfWork>();

    /// <summary>Stages the accounts and flushes them through the unit of work (independent of the read under test).</summary>
    protected static async Task Seed(CassandraTestDatabase db, params Account[] accounts)
    {
        var repo = AccountRepo(db);
        foreach (var account in accounts)
        {
            await repo.AddAsync(account);
        }

        await Uow(db).CommitAsync();
    }

    /// <summary>Stages the readings and flushes them through the unit of work.</summary>
    protected static async Task Seed(CassandraTestDatabase db, params Reading[] readings)
    {
        var repo = ReadingRepo(db);
        foreach (var reading in readings)
        {
            await repo.AddAsync(reading);
        }

        await Uow(db).CommitAsync();
    }
}

/// <summary>A single test's keyspace, with the provider's services registered and a resolution scope open.</summary>
public sealed class CassandraTestDatabase : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;

    public CassandraTestDatabase(string host, int port, string keyspace,
        Action<CassandraModelBuilder> model, Assembly[] migrationAssemblies,
        Action<IServiceCollection>? configure = null)
    {
        Keyspace = keyspace;

        var services = new ServiceCollection();
        services.AddCassandraSession(keyspace, [host], port);
        services.AddCassandraRepositories(model);
        if (migrationAssemblies.Length > 0)
        {
            services.AddCassandraMigrations(migrationAssemblies);
        }

        configure?.Invoke(services);

        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
        Session = _scope.ServiceProvider.GetRequiredService<ISession>();
    }

    /// <summary>The keyspace name (unique to this test).</summary>
    public string Keyspace { get; }

    /// <summary>The session, bound to this test's keyspace (for raw schema inspection).</summary>
    public ISession Session { get; }

    public T Resolve<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    public void Dispose()
    {
        // Capture the cluster before disposing the container-owned session, then dispose it so the control
        // connection and per-host pools of this test's cluster are released (no leak across the run).
        var cluster = Session.Cluster;
        _scope.Dispose();
        _provider.Dispose();
        cluster.Dispose();
    }
}
