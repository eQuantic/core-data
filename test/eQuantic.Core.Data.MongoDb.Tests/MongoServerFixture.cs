using System.Reflection;
using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.MongoDb.Extensions;
using EphemeralMongo;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Xunit;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>
///     Boots a real, ephemeral <c>mongod</c> once for the whole test collection (as a single-node replica set,
///     so explicit transactions work). Each test takes a fresh, isolated database.
/// </summary>
public sealed class MongoServerFixture : IDisposable
{
    private readonly IMongoRunner _runner;

    public MongoServerFixture()
    {
        _runner = MongoRunner.Run(new MongoRunnerOptions { UseSingleNodeReplicaSet = true });
        Client = new MongoClient(_runner.ConnectionString);
    }

    public IMongoClient Client { get; }

    /// <summary>Creates a fresh, isolated database wired through the provider's DI registrations.</summary>
    public MongoTestDatabase NewDatabase(params Assembly[] migrationAssemblies) =>
        new(Client, "test_" + Guid.NewGuid().ToString("N"), migrationAssemblies);

    public void Dispose() => _runner.Dispose();
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

[CollectionDefinition("mongo")]
public sealed class MongoTestCollection : ICollectionFixture<MongoServerFixture>;
