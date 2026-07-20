using System.Reflection;
using eQuantic.Core.Data.MongoDb.Extensions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>
///     Boots one real MongoDB container for the whole test run — a single-node replica set, so explicit
///     transactions work. Runs the assertions against a genuine <c>mongod</c> (Testcontainers), and skips
///     gracefully when Docker is not available (e.g. a runner without a Linux container engine).
/// </summary>
[SetUpFixture]
public sealed class MongoTestServer
{
    private static MongoDbContainer? _container;
    private static Exception? _startupError;

    /// <summary>The client connected to the test container.</summary>
    public static IMongoClient Client { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        try
        {
            _container = new MongoDbBuilder()
                .WithImage("mongo:7.0")
                .WithReplicaSet()
                .Build();
            await _container.StartAsync();

            // Talk straight to the mapped port: the single-node replica set is initiated with the container's
            // internal host, which the driver cannot reach from the host during topology discovery. A direct
            // connection to the (primary) node keeps writes and transactions working.
            var settings = MongoClientSettings.FromConnectionString(_container.GetConnectionString());
            settings.DirectConnection = true;
            Client = new MongoClient(settings);
        }
        catch (Exception ex)
        {
            _startupError = ex;
        }
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        (Client as IDisposable)?.Dispose();
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
            Assert.Ignore($"MongoDB test container is unavailable (Docker required): {_startupError.Message}");
        }
    }

    /// <summary>Creates a fresh, isolated database wired through the provider's DI registrations.</summary>
    public static MongoTestDatabase NewDatabase(params Assembly[] migrationAssemblies) =>
        new(Client, "test_" + Guid.NewGuid().ToString("N"), migrationAssemblies);
}

/// <summary>Base class that skips a test when the MongoDB container is unavailable.</summary>
public abstract class MongoIntegrationTest
{
    [SetUp]
    public void RequireDocker() => MongoTestServer.EnsureAvailable();
}

/// <summary>A single test's database, with the provider's services registered and a resolution scope open.</summary>
public sealed class MongoTestDatabase : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;

    public MongoTestDatabase(IMongoClient client, string databaseName, Assembly[] migrationAssemblies)
    {
        Database = client.GetDatabase(databaseName);

        var services = new ServiceCollection();
        services.AddSingleton(client);
        services.AddSingleton(Database);
        services.AddMongoRepositories();
        if (migrationAssemblies.Length > 0)
        {
            services.AddMongoMigrations(migrationAssemblies);
        }

        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
    }

    public IMongoDatabase Database { get; }

    public T Resolve<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
    }
}
