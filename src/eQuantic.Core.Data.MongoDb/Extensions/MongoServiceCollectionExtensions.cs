using System.Reflection;
using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.MongoDb.Migration;
using eQuantic.Core.Data.MongoDb.Repository;
using eQuantic.Core.Data.Repository;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb.Extensions;

/// <summary>
///     Registers the native MongoDB data services: the client/database handles, the unit of work and the
///     generic repositories, and (optionally) the document-store migration runner.
/// </summary>
public static class MongoServiceCollectionExtensions
{
    /// <summary>Registers the MongoDB client (singleton) and database (singleton) from a connection string.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The MongoDB connection string.</param>
    /// <param name="databaseName">The target database name.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddMongoDatabase(this IServiceCollection services, string connectionString, string databaseName)
    {
        services.TryAddSingleton<IMongoClient>(_ => new MongoClient(connectionString));
        services.TryAddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(databaseName));
        return services;
    }

    /// <summary>
    ///     Registers the generic repositories over the <see cref="MongoDefaultUnitOfWork" />. Call
    ///     <see cref="AddMongoDatabase" /> too (or an overload that takes a connection string) so the client and
    ///     database are available.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="lifetime">The unit-of-work and repository lifetime (scoped by default).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddMongoRepositories(this IServiceCollection services, ServiceLifetime lifetime = ServiceLifetime.Scoped) =>
        services.AddMongoRepositories<MongoDefaultUnitOfWork>(lifetime);

    /// <summary>Registers the generic repositories over a custom <typeparamref name="TUnitOfWork" />.</summary>
    /// <typeparam name="TUnitOfWork">The unit-of-work implementation.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="lifetime">The unit-of-work and repository lifetime (scoped by default).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddMongoRepositories<TUnitOfWork>(this IServiceCollection services, ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TUnitOfWork : MongoUnitOfWork
    {
        services.TryAdd(new ServiceDescriptor(typeof(TUnitOfWork), typeof(TUnitOfWork), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(IQueryableUnitOfWork), sp => sp.GetRequiredService<TUnitOfWork>(), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(IUnitOfWork), sp => sp.GetRequiredService<TUnitOfWork>(), lifetime));

        services.TryAdd(new ServiceDescriptor(typeof(IRepository<,>), typeof(MongoRepository<,>), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(IQueryableRepository<,>), typeof(MongoRepository<,>), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(IAsyncRepository<,>), typeof(MongoRepository<,>), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(IAsyncQueryableRepository<,>), typeof(MongoRepository<,>), lifetime));

        return services;
    }

    /// <summary>Registers the client, database and repositories in one call.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The MongoDB connection string.</param>
    /// <param name="databaseName">The target database name.</param>
    /// <param name="lifetime">The unit-of-work and repository lifetime (scoped by default).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddMongoRepositories(this IServiceCollection services, string connectionString, string databaseName,
        ServiceLifetime lifetime = ServiceLifetime.Scoped) =>
        services.AddMongoDatabase(connectionString, databaseName).AddMongoRepositories(lifetime);

    /// <summary>
    ///     Registers the document-store migration runner, executor and history. Migrations are discovered in the
    ///     supplied assemblies (the calling assembly when none are given). Resolve <see cref="IMigrationRunner" />
    ///     and call <c>RunAsync</c> on startup.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="assemblies">The assemblies scanned for migrations; the calling assembly when empty.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddMongoMigrations(this IServiceCollection services, params Assembly[] assemblies)
    {
        var scanned = assemblies.Length > 0 ? assemblies : [Assembly.GetCallingAssembly()];

        services.TryAddScoped<IMigrationExecutor>(sp => new MongoMigrationExecutor(sp.GetRequiredService<IMongoDatabase>()));
        services.TryAddScoped<IMigrationHistory>(sp => new MongoMigrationHistory(sp.GetRequiredService<IMongoDatabase>()));
        services.TryAddScoped<IMigrationRunner>(sp => new MongoMigrationRunner(
            sp.GetRequiredService<IMigrationExecutor>(),
            sp.GetRequiredService<IMigrationHistory>(),
            scanned));

        return services;
    }
}
