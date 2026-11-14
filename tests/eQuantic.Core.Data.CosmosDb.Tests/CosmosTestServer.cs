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
    public const string SecretsContainer = "secrets";
    public const string UsersContainer = "users";

    private static readonly string[] RelationContainers =
        ["rel_items", "rel_mentions", "rel_runs", "rel_boxes", "rel_artifacts", "rel_repos", "rel_spaces"];

    // The well-known emulator account key (identical across the classic and vNext emulators).
    private const string EmulatorKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    private static IContainer? _container;
    private static CosmosClient? _client;
    private static ServiceProvider? _provider;
    private static Exception? _startupError;

    /// <summary>Counts the requests the shared client has in flight against one container, while a test watches.</summary>
    public static InFlightRequests Requests { get; } = new();

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
            options.CustomHandlers.Add(Requests);
            _client = new CosmosClient(endpoint, EmulatorKey, options);

            // The gateway health probe flips to ready before the vNext data-plane (the pgcosmos engine) finishes
            // starting, which answers early requests with 503 "extension is still starting; retry shortly". Gate
            // the bootstrap on the data plane being genuinely up rather than racing it.
            var database = await BootstrapAsync(async () =>
                (await _client.CreateDatabaseIfNotExistsAsync(DatabaseName)).Database);
            await BootstrapAsync(() => database.CreateContainerIfNotExistsAsync(new ContainerProperties(ContainerName, "/category")));

            // The write-outcome tests' containers hold their targets unique: a unique key per partition.
            await BootstrapAsync(() => database.CreateContainerIfNotExistsAsync(new ContainerProperties(SecretsContainer, "/category")
            {
                UniqueKeyPolicy = new UniqueKeyPolicy { UniqueKeys = { new UniqueKey { Paths = { "/workspaceId", "/name" } } } },
            }));
            await BootstrapAsync(() => database.CreateContainerIfNotExistsAsync(new ContainerProperties(UsersContainer, "/category")
            {
                UniqueKeyPolicy = new UniqueKeyPolicy { UniqueKeys = { new UniqueKey { Paths = { "/email" } } } },
            }));

            // The query-predicate entities each live in a container of their own: a relation crosses containers.
            foreach (var relationContainer in RelationContainers)
            {
                await BootstrapAsync(() => database.CreateContainerIfNotExistsAsync(new ContainerProperties(relationContainer, "/category")));
            }

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
    private static void RegisterEntities(CosmosModelBuilder model) => ReadShapesModel.Configure(model)
        .Converts<GadgetGrade, string>(
            grade => grade.ToString().ToLowerInvariant(),
            stored => Enum.Parse<GadgetGrade>(stored, ignoreCase: true))
        .Entity<CosmosProduct>(entity => entity
            .Container(ContainerName)
            .PartitionKey(x => x.Category)
            .ConcurrencyToken(x => x.ETag))
        .Entity<RenamedGadget>(entity => entity
            .Container(ContainerName)
            .PartitionKey(x => x.Category))
        .Entity<GeoEvent>(entity => entity
            .Container("geo_events"))
        .Enum<Channel>()
        .Entity<RunRecord>(entity => entity
            .Container(ContainerName)
            .PartitionKey(x => x.Category))
        .Entity<Receipt>(entity => entity
            .Container(ContainerName)
            .PartitionKey(x => x.Category)
            .Generated(x => x.Number)
            .Generated(x => x.IssuedAt, eQuantic.Core.Data.Modeling.StoreValue.Clock))
        .Entity<Secret>(entity => entity.Container(SecretsContainer).PartitionKey(x => x.Category))
        .Entity<UserAccount>(entity => entity.Container(UsersContainer).PartitionKey(x => x.Category))
        .Entity<Label>(entity => entity.Container(ContainerName).PartitionKey(x => x.Category))
        .Entity<Job>(entity => entity.Container(ContainerName).PartitionKey(x => x.Category))
        .Entity<ItemType>(entity => entity.Container(ContainerName).PartitionKey(x => x.Category))
        .Entity<Page>(entity => entity.Container(ContainerName).PartitionKey(x => x.Category))
        .Entity<WideRecord>(entity => entity.Container(ContainerName).PartitionKey(x => x.Category).ConcurrencyToken(x => x.ETag))
        .Entity<RelItem>(entity => entity.Container("rel_items").PartitionKey(x => x.Category))
        .Entity<RelMention>(entity => entity.Container("rel_mentions").PartitionKey(x => x.Category))
        .Entity<RelRun>(entity => entity.Container("rel_runs").PartitionKey(x => x.Category))
        .Entity<RelBox>(entity => entity.Container("rel_boxes").PartitionKey(x => x.Category))
        .Entity<RelArtifact>(entity => entity.Container("rel_artifacts").PartitionKey(x => x.Category))
        .Entity<RelRepo>(entity => entity.Container("rel_repos").PartitionKey(x => x.Category))
        .Entity<RelSpace>(entity => entity.Container("rel_spaces").PartitionKey(x => x.Category))
        .Entity<RelCompany>(entity => entity.Container(ContainerName).PartitionKey(x => x.Category));

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

/// <summary>
///     A request handler of the shared client that counts the requests in flight against one container while a test
///     watches it, and the most it saw at once. Retries run below the custom handlers, so each request counts once.
/// </summary>
public sealed class InFlightRequests : RequestHandler
{
    private string? _watched;
    private int _current;
    private int _peak;

    /// <summary>The most requests against the watched container seen in flight at once since <see cref="Watch" />.</summary>
    public int Peak => Volatile.Read(ref _peak);

    /// <summary>Starts counting the requests against <paramref name="container" />, from zero.</summary>
    public void Watch(string container)
    {
        Volatile.Write(ref _peak, 0);
        Volatile.Write(ref _watched, "/colls/" + container + "/");
    }

    /// <summary>Stops counting.</summary>
    public void Stop() => Volatile.Write(ref _watched, null);

    public override async Task<ResponseMessage> SendAsync(RequestMessage request, CancellationToken cancellationToken)
    {
        var watched = Volatile.Read(ref _watched);
        if (watched is null || request.RequestUri?.OriginalString.Contains(watched, StringComparison.Ordinal) != true)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var current = Interlocked.Increment(ref _current);
        for (var peak = Volatile.Read(ref _peak); current > peak; peak = Volatile.Read(ref _peak))
        {
            if (Interlocked.CompareExchange(ref _peak, current, peak) == peak)
            {
                break;
            }
        }

        try
        {
            return await base.SendAsync(request, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _current);
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
