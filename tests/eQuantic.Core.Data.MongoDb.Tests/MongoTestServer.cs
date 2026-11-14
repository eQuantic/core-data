using System.Reflection;
using eQuantic.Core.Data.MongoDb.Extensions;
using eQuantic.Core.Data.MongoDb.Repository;
using eQuantic.Core.Data.Repository;
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

            // The container is up before its replica set has elected the node primary: a write sent before then is
            // refused or retried by the driver once the election is over, which a test that times its first write
            // (a lease's expiry) reads as the store's own behaviour. The run starts once the node takes writes.
            await WaitForPrimaryAsync(Client);
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

    private static async Task WaitForPrimaryAsync(IMongoClient client)
    {
        var admin = client.GetDatabase("admin");
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            try
            {
                var hello = await admin.RunCommandAsync<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("hello", 1));
                if (hello.GetValue("isWritablePrimary", false).ToBoolean())
                {
                    return;
                }
            }
            catch (MongoException) when (DateTime.UtcNow < deadline)
            {
                // Not answering yet: asked again below.
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The MongoDB test container's replica set elected no primary within a minute.");
            }

            await Task.Delay(100);
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
        new(Client, "test_" + Guid.NewGuid().ToString("N"), migrationAssemblies, null);

    /// <summary>Creates a fresh database with extra registrations (e.g. global query filters).</summary>
    public static MongoTestDatabase NewDatabase(Action<IServiceCollection> configure) =>
        new(Client, "test_" + Guid.NewGuid().ToString("N"), [], configure);
}

/// <summary>Base class that skips a test when the MongoDB container is unavailable, with shared helpers.</summary>
public abstract class MongoIntegrationTest
{
    [SetUp]
    public void RequireDocker() => MongoTestServer.EnsureAvailable();

    protected static MongoTestDatabase NewDatabase(params Assembly[] migrationAssemblies) =>
        MongoTestServer.NewDatabase(migrationAssemblies);

    protected static MongoTestDatabase NewDatabase(Action<IServiceCollection> configure) =>
        MongoTestServer.NewDatabase(configure);

    protected static IAsyncRepository<Product, string> AsyncRepo(MongoTestDatabase db) =>
        db.Resolve<IAsyncRepository<Product, string>>();

    protected static IRepository<Product, string> SyncRepo(MongoTestDatabase db) =>
        db.Resolve<IRepository<Product, string>>();

    protected static MongoDefaultUnitOfWork Uow(MongoTestDatabase db) =>
        db.Resolve<MongoDefaultUnitOfWork>();

    /// <summary>Inserts documents straight through the driver (independent of the repository under test).</summary>
    protected static Task Seed(MongoTestDatabase db, params Product[] products) =>
        db.Database.GetCollection<Product>("Product").InsertManyAsync(products);
}

/// <summary>A single test's database, with the provider's services registered and a resolution scope open.</summary>
public sealed class MongoTestDatabase : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;

    public MongoTestDatabase(IMongoClient client, string databaseName, Assembly[] migrationAssemblies,
        Action<IServiceCollection>? configure = null)
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

        configure?.Invoke(services);

        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
    }

    public IMongoDatabase Database { get; }

    public T Resolve<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    /// <summary>A scope of its own — its own unit of work — over the same database.</summary>
    public IServiceScope NewScope() => _provider.CreateScope();

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
    }
}
