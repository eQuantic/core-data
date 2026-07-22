using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using eQuantic.Core.Data.CosmosDb.Extensions;
using eQuantic.Core.Data.CosmosDb.Repository;
using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Repository;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.CosmosDb.Tests;

/// <summary>
///     Boots one Azure Cosmos DB <b>vNext</b> emulator for the whole test run (Testcontainers) and builds a
///     single shared container + service provider. The vNext emulator runs on every architecture (Linux/macOS/
///     Windows, x64/ARM64), serves the gateway over plain HTTP and exposes a health probe, so readiness is gated
///     on <c>/ready</c> instead of racing — no more spurious 408/503 collapses. Cosmos container creation is slow,
///     so the suite creates the container once and isolates tests by a unique partition-key value. Skips
///     gracefully when Docker is unavailable.
/// </summary>
[SetUpFixture]
public sealed class CosmosTestServer
{
    public const string DatabaseName = "tests";
    public const string ContainerName = "products";

    // The well-known emulator account key (identical across the classic and vNext emulators).
    private const string EmulatorKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    private static IContainer? _container;
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
            // Gateway on 8081 (HTTP), health probe on 8080; the wait strategy blocks until /ready returns 200.
            _container = new ContainerBuilder()
                .WithImage("mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-latest")
                .WithPortBinding(8081, true)
                .WithPortBinding(8080, true)
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(request => request.ForPort(8080).ForPath("/ready")))
                .Build();
            await _container.StartAsync();

            var endpoint = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(8081)}/";

            // One model instance feeds both the client's serializer (renames, exclusions, Converts) and DI.
            var modelBuilder = new CosmosModelBuilder();
            RegisterEntities(modelBuilder);
            var model = modelBuilder.Build();

            var options = new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                LimitToEndpoint = true,
                Serializer = new CosmosEntitySerializer(CosmosEntitySerializer.BuildOptions(model)),
                RequestTimeout = TimeSpan.FromMinutes(2),
            };
            _client = new CosmosClient(endpoint, EmulatorKey, options);

            // The gateway health probe flips to ready before the vNext data-plane (the pgcosmos engine) finishes
            // starting, which answers early requests with 503 "extension is still starting; retry shortly". Gate
            // the bootstrap on the data plane being genuinely up rather than racing it.
            var database = await BootstrapAsync(async () =>
                (await _client.CreateDatabaseIfNotExistsAsync(DatabaseName)).Database);
            await BootstrapAsync(() => database.CreateContainerIfNotExistsAsync(new ContainerProperties(ContainerName, "/category")));

            var services = new ServiceCollection();
            services.AddSingleton(_client);

            // A per-request tenant read by the global-filter factory: tests that set it get scoped reads/writes,
            // tests that don't are untouched (the factory returns null).
            services.AddScoped<TenantBox>();
            services.AddSingleton(new QueryFilters().For<CosmosProduct>(scope =>
                scope.GetService(typeof(TenantBox)) is TenantBox { Category: { } category }
                    ? product => product.Category == category
                    : null));

            services.AddSingleton(model);
            services.AddCosmosDatabase("emulator", DatabaseName, RegisterEntities);
            services.AddCosmosRepositories();
            services.AddCosmosMigrations(typeof(CosmosProductsSetupMigration).Assembly);
            _provider = services.BuildServiceProvider();
        }
        catch (Exception ex)
        {
            _startupError = ex;
        }
    }

    /// <summary>The entity model shared by the client's serializer and the DI registration.</summary>
    private static void RegisterEntities(CosmosModelBuilder model) => model
        .Converts<GadgetGrade, string>(
            grade => grade.ToString().ToLowerInvariant(),
            stored => Enum.Parse<GadgetGrade>(stored, ignoreCase: true))
        .Entity<CosmosProduct>(entity => entity
            .Container(ContainerName)
            .PartitionKey(x => x.Category)
            .ConcurrencyToken(x => x.ETag))
        .Entity<RenamedGadget>(entity => entity
            .Container(ContainerName)
            .PartitionKey(x => x.Category));

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

    /// <summary>Runs a bootstrap step, retrying the "pgcosmos still starting" 503 until the data plane is up.</summary>
    private static async Task<T> BootstrapAsync<T>(Func<Task<T>> step)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            try
            {
                return await step();
            }
            catch (CosmosException exception) when (
                exception.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }

    private static Task BootstrapAsync(Func<Task> step) => BootstrapAsync(async () =>
    {
        await step();
        return true;
    });

    /// <summary>Skips the calling test when the emulator could not start.</summary>
    public static void EnsureAvailable()
    {
        if (_startupError is not null)
        {
            Assert.Ignore($"Cosmos emulator is unavailable (Docker required): {_startupError.Message}");
        }
    }
}

/// <summary>The per-scope tenant the global-filter factory reads; unset (null) applies no filter.</summary>
public sealed class TenantBox
{
    /// <summary>The tenant's partition value, or <c>null</c> for no scoping.</summary>
    public string? Category { get; set; }
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

    /// <summary>Resolves a service from this test's scope.</summary>
    protected T Resolve<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

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
