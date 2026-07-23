using System.Reflection;
using eQuantic.Core.Data.CosmosDb.Migration;
using eQuantic.Core.Data.CosmosDb.Repository;
using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Repository;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace eQuantic.Core.Data.CosmosDb.Extensions;

/// <summary>
///     Registers the native Azure Cosmos DB data services: the client and database handles, the entity model
///     (containers and partition keys), the unit of work and the generic repositories, and (optionally) the
///     document-store migration runner.
/// </summary>
public static class CosmosServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the Cosmos client (singleton), the database (singleton, created if missing) and the entity
    ///     model built from <paramref name="model" />.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The Cosmos connection string.</param>
    /// <param name="databaseName">The target database name.</param>
    /// <param name="model">Builds the entity model (container names, partition keys, TTL).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCosmosDatabase(this IServiceCollection services, string connectionString,
        string databaseName, Action<CosmosModelBuilder> model)
    {
        var builder = new CosmosModelBuilder();
        model(builder);

        services.TryAddSingleton(builder.Build());
        services.TryAddSingleton(serviceProvider =>
            CosmosClientFactory.Create(connectionString, serviceProvider.GetRequiredService<CosmosModel>(),
                serviceProvider.GetService(typeof(Microsoft.Extensions.Logging.ILoggerFactory))
                    as Microsoft.Extensions.Logging.ILoggerFactory));
        services.TryAddSingleton(serviceProvider => serviceProvider.GetRequiredService<CosmosClient>()
            .CreateDatabaseIfNotExistsAsync(databaseName).GetAwaiter().GetResult().Database);

        return services;
    }

    /// <summary>Registers the generic repositories over the <see cref="CosmosDefaultUnitOfWork" />.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="lifetime">The unit-of-work and repository lifetime (scoped by default).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCosmosRepositories(this IServiceCollection services, ServiceLifetime lifetime = ServiceLifetime.Scoped) =>
        services.AddCosmosRepositories<CosmosDefaultUnitOfWork>(lifetime);

    /// <summary>Registers the generic repositories over a custom <typeparamref name="TUnitOfWork" />.</summary>
    /// <typeparam name="TUnitOfWork">The unit-of-work implementation.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="lifetime">The unit-of-work and repository lifetime (scoped by default).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCosmosRepositories<TUnitOfWork>(this IServiceCollection services, ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TUnitOfWork : CosmosUnitOfWork
    {
        services.TryAdd(new ServiceDescriptor(typeof(TUnitOfWork), typeof(TUnitOfWork), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(IQueryableUnitOfWork), sp => sp.GetRequiredService<TUnitOfWork>(), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(IUnitOfWork), sp => sp.GetRequiredService<TUnitOfWork>(), lifetime));

        services.TryAdd(new ServiceDescriptor(typeof(IRepository<,>), typeof(CosmosRepository<,>), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(IQueryableRepository<,>), typeof(CosmosRepository<,>), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(IAsyncRepository<,>), typeof(CosmosRepository<,>), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(IAsyncQueryableRepository<,>), typeof(CosmosRepository<,>), lifetime));

        return services;
    }

    /// <summary>Registers the client, database, model and repositories in one call.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The Cosmos connection string.</param>
    /// <param name="databaseName">The target database name.</param>
    /// <param name="model">Builds the entity model.</param>
    /// <param name="lifetime">The unit-of-work and repository lifetime (scoped by default).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCosmosRepositories(this IServiceCollection services, string connectionString,
        string databaseName, Action<CosmosModelBuilder> model, ServiceLifetime lifetime = ServiceLifetime.Scoped) =>
        services.AddCosmosDatabase(connectionString, databaseName, model).AddCosmosRepositories(lifetime);

    /// <summary>
    ///     Registers the document-store migration runner, executor and history. Migrations are discovered in the
    ///     supplied assemblies (the calling assembly when none are given). Resolve <see cref="IMigrationRunner" />
    ///     and call <c>RunAsync</c> on startup.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="assemblies">The assemblies scanned for migrations; the calling assembly when empty.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCosmosMigrations(this IServiceCollection services, params Assembly[] assemblies)
    {
        var scanned = assemblies.Length > 0 ? assemblies : [Assembly.GetCallingAssembly()];

        services.TryAddScoped<IMigrationExecutor>(sp => new CosmosMigrationExecutor(
            sp.GetRequiredService<Database>(), sp.GetRequiredService<CosmosModel>()));
        services.TryAddScoped<IMigrationHistory>(sp => new CosmosMigrationHistory(sp.GetRequiredService<Database>()));
        services.TryAddScoped<IMigrationRunner>(sp => new CosmosMigrationRunner(
            sp.GetRequiredService<IMigrationExecutor>(), sp.GetRequiredService<IMigrationHistory>(), scanned,
            sp.GetService(typeof(MigrationSource)) as MigrationSource));

        return services;
    }

    /// <summary>
    ///     Registers the migration runner over <b>explicitly named</b> migrations — the trim/NativeAOT-safe
    ///     form, since nothing is discovered by reflection:
    ///     <code>services.AddCosmosMigrations(source => source.Add&lt;ProductsSetup&gt;());</code>
    ///     Ordering still comes from each migration's <c>[Migration]</c> timestamp.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="migrations">Registers the migrations to run.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCosmosMigrations(this IServiceCollection services,
        Action<MigrationSource> migrations)
    {
        var source = new MigrationSource();
        migrations(source);
        services.TryAddSingleton(source);

        services.TryAddScoped<IMigrationExecutor>(sp => new CosmosMigrationExecutor(
            sp.GetRequiredService<Database>(), sp.GetRequiredService<CosmosModel>()));
        services.TryAddScoped<IMigrationHistory>(sp => new CosmosMigrationHistory(sp.GetRequiredService<Database>()));
        services.TryAddScoped<IMigrationRunner>(sp => new CosmosMigrationRunner(
            sp.GetRequiredService<IMigrationExecutor>(), sp.GetRequiredService<IMigrationHistory>(),
            [], sp.GetRequiredService<MigrationSource>()));

        return services;
    }
}
