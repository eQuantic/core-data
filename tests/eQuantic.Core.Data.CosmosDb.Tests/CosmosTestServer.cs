using System.Reflection;
using eQuantic.Core.Data.CosmosDb.Extensions;
using eQuantic.Core.Data.CosmosDb.Repository;
using eQuantic.Core.Data.Repository;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.CosmosDb;

namespace eQuantic.Core.Data.CosmosDb.Tests;

/// <summary>
///     Boots one Azure Cosmos DB Linux emulator for the whole test run (Testcontainers), building a client that
///     trusts the emulator's certificate and talks over the gateway. Skips gracefully when Docker/the emulator is
///     unavailable. Because Cosmos container creation is slow, each test uses its own container.
/// </summary>
[SetUpFixture]
public sealed class CosmosTestServer
{
    public const string DatabaseName = "tests";

    private static CosmosDbContainer? _container;
    private static Exception? _startupError;

    /// <summary>The client connected to the emulator.</summary>
    public static CosmosClient Client { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        try
        {
            // The Testcontainers.CosmosDb wait strategy targets the classic emulator (amd64). It runs on the CI
            // Linux (amd64) runners; on an arm64 host the emulator image is unavailable, so the tests skip.
            _container = new CosmosDbBuilder().Build();
            await _container.StartAsync();

            var options = new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                HttpClientFactory = () => _container.HttpClient,
                UseSystemTextJsonSerializerWithOptions = CosmosClientFactory.SerializerOptions,
                AllowBulkExecution = true,
            };
            Client = new CosmosClient(_container.GetConnectionString(), options);
            await Client.CreateDatabaseIfNotExistsAsync(DatabaseName);
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

    /// <summary>Skips the calling test when the emulator could not start.</summary>
    public static void EnsureAvailable()
    {
        if (_startupError is not null)
        {
            Assert.Ignore($"Cosmos emulator is unavailable (Docker required): {_startupError.Message}");
        }
    }

    /// <summary>Creates a fresh, isolated container wired through the provider's DI registrations.</summary>
    public static CosmosTestDatabase NewDatabase(bool createContainer = true, params Assembly[] migrationAssemblies) =>
        new(Client, DatabaseName, "c" + Guid.NewGuid().ToString("N"), createContainer, migrationAssemblies);
}

/// <summary>Base class that skips a test when the emulator is unavailable.</summary>
public abstract class CosmosIntegrationTest
{
    [SetUp]
    public void RequireEmulator() => CosmosTestServer.EnsureAvailable();

    protected static CosmosTestDatabase NewDatabase(bool createContainer = true, params Assembly[] migrationAssemblies) =>
        CosmosTestServer.NewDatabase(createContainer, migrationAssemblies);

    protected static IAsyncRepository<CosmosProduct, string> Repo(CosmosTestDatabase db) =>
        db.Resolve<IAsyncRepository<CosmosProduct, string>>();

    protected static CosmosDefaultUnitOfWork Uow(CosmosTestDatabase db) => db.Resolve<CosmosDefaultUnitOfWork>();

    protected static async Task Seed(CosmosTestDatabase db, params CosmosProduct[] products)
    {
        var repository = Repo(db);
        foreach (var product in products)
        {
            await repository.AddAsync(product);
        }

        await Uow(db).CommitAsync();
    }
}

/// <summary>A single test's container, with the provider's services registered and a resolution scope open.</summary>
public sealed class CosmosTestDatabase : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;

    public CosmosTestDatabase(CosmosClient client, string databaseName, string containerName, bool createContainer, Assembly[] migrationAssemblies)
    {
        ContainerName = containerName;

        var services = new ServiceCollection();
        services.AddSingleton(client);
        services.AddCosmosDatabase("emulator", databaseName, model =>
            model.Entity<CosmosProduct>(entity => entity.Container(containerName).PartitionKey(x => x.Category)));
        services.AddCosmosRepositories();
        if (migrationAssemblies.Length > 0)
        {
            services.AddCosmosMigrations(migrationAssemblies);
        }

        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();

        if (createContainer)
        {
            Database.CreateContainerIfNotExistsAsync(new ContainerProperties(containerName, "/category")).GetAwaiter().GetResult();
        }
    }

    public string ContainerName { get; }

    public Database Database => Resolve<Database>();

    public T Resolve<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
    }
}
