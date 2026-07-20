using eQuantic.Core.Data.CosmosDb.Extensions;
using eQuantic.Core.Data.CosmosDb.Repository;
using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Repository;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.CosmosDb;

namespace eQuantic.Core.Data.CosmosDb.Tests;

/// <summary>
///     Boots one Azure Cosmos DB Linux emulator for the whole test run (Testcontainers) and builds a single
///     shared container + service provider. Cosmos container creation is slow and overwhelms the emulator, so the
///     suite creates the container once and isolates tests by a unique partition-key value instead of a container
///     per test. Skips gracefully when Docker/the emulator is unavailable.
/// </summary>
[SetUpFixture]
public sealed class CosmosTestServer
{
    public const string DatabaseName = "tests";
    public const string ContainerName = "products";

    private static CosmosDbContainer? _container;
    private static CosmosClient? _client;
    private static ServiceProvider? _provider;
    private static Exception? _startupError;

    /// <summary>The shared service provider (repositories, unit of work, migrations) over the emulator.</summary>
    public static ServiceProvider Provider => _provider!;

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        try
        {
            _container = new CosmosDbBuilder().Build();
            await _container.StartAsync();

            var options = new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                HttpClientFactory = () => _container.HttpClient,
                UseSystemTextJsonSerializerWithOptions = CosmosClientFactory.SerializerOptions,
                AllowBulkExecution = true,
                RequestTimeout = TimeSpan.FromMinutes(5),
            };
            _client = new CosmosClient(_container.GetConnectionString(), options);

            var database = (await _client.CreateDatabaseIfNotExistsAsync(DatabaseName)).Database;
            await database.CreateContainerIfNotExistsAsync(new ContainerProperties(ContainerName, "/category"));

            var services = new ServiceCollection();
            services.AddSingleton(_client);
            services.AddCosmosDatabase("emulator", DatabaseName, model =>
                model.Entity<CosmosProduct>(entity => entity.Container(ContainerName).PartitionKey(x => x.Category)));
            services.AddCosmosRepositories();
            services.AddCosmosMigrations(typeof(CosmosProductsSetupMigration).Assembly);
            _provider = services.BuildServiceProvider();
        }
        catch (Exception ex)
        {
            _startupError = ex;
        }
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        _provider?.Dispose();
        _client?.Dispose();
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
}

/// <summary>
///     Base for the Cosmos integration tests: skips when the emulator is unavailable, opens a fresh DI scope per
///     test, and hands each test a unique partition value (<see cref="Partition" />) so they share one container
///     without colliding.
/// </summary>
public abstract class CosmosIntegrationTest
{
    private IServiceScope _scope = null!;

    /// <summary>A unique partition-key value for this test (isolation within the shared container).</summary>
    protected string Partition { get; private set; } = null!;

    protected IAsyncRepository<CosmosProduct, string> Repo =>
        _scope.ServiceProvider.GetRequiredService<IAsyncRepository<CosmosProduct, string>>();

    protected CosmosDefaultUnitOfWork Uow => _scope.ServiceProvider.GetRequiredService<CosmosDefaultUnitOfWork>();

    protected Database Database => _scope.ServiceProvider.GetRequiredService<Database>();

    protected IMigrationRunner Runner => _scope.ServiceProvider.GetRequiredService<IMigrationRunner>();

    [SetUp]
    public void SetUp()
    {
        CosmosTestServer.EnsureAvailable();
        Partition = "p" + Guid.NewGuid().ToString("N");
        _scope = CosmosTestServer.Provider.CreateScope();
    }

    [TearDown]
    public void TearDown() => _scope?.Dispose();

    /// <summary>Seeds the products and commits.</summary>
    protected async Task Seed(params CosmosProduct[] products)
    {
        foreach (var product in products)
        {
            await Repo.AddAsync(product);
        }

        await Uow.CommitAsync();
    }
}
