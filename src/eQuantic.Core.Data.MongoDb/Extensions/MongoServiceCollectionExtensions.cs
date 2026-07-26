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
        MongoModeling.Register();
        services.TryAddSingleton<IMongoClient>(serviceProvider =>
        {
            var settings = MongoClientSettings.FromConnectionString(connectionString);

            // The driver's command events feed the engine's logs and metrics — the same category/event-id
            // discipline every provider follows; bodies (which carry values) only log behind the opt-in.
            var logger = (serviceProvider.GetService(typeof(Microsoft.Extensions.Logging.ILoggerFactory))
                    as Microsoft.Extensions.Logging.ILoggerFactory)
                ?.CreateLogger("eQuantic.Core.Data.mongodb.Command");
            var sensitive = (serviceProvider.GetService(typeof(eQuantic.Core.Data.Repository.DataConventions))
                as eQuantic.Core.Data.Repository.DataConventions)?.EnableSensitiveDataLogging ?? false;
            settings.ClusterConfigurator = cluster => Diagnostics.MongoCommandLogging.Subscribe(cluster, logger, sensitive);

            return new MongoClient(settings);
        });
        services.TryAddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(databaseName));
        return services;
    }

    /// <summary>
    ///     Registers the client and database, and applies the fluent entity model — collection names, id members,
    ///     element renames, exclusions and value conversions (conventions &lt; annotations &lt; fluent).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The MongoDB connection string.</param>
    /// <param name="databaseName">The target database name.</param>
    /// <param name="model">Builds the entity model.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddMongoDatabase(this IServiceCollection services, string connectionString,
        string databaseName, Action<MongoModelBuilder> model)
    {
        var builder = new MongoModelBuilder();
        model(builder);
        services.TryAddSingleton(builder.Build());

        // Describes the model for the tooling that compares one version of it against another.
        services.TryAddSingleton<Data.Evolution.IModelSnapshotSource>(provider =>
            new Evolution.MongoModelSnapshotSource(provider.GetRequiredService<MongoModel>()));

        return services.AddMongoDatabase(connectionString, databaseName);
    }

    /// <summary>
    ///     Registers the generic repositories over the <see cref="MongoDefaultUnitOfWork" />. Call
    ///     <see cref="AddMongoDatabase(IServiceCollection, string, string)" /> too (or an overload that takes a
    ///     connection string) so the client and database are available.
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
        MongoModeling.Register();
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

    /// <summary>Registers the client, database, fluent entity model and repositories in one call.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The MongoDB connection string.</param>
    /// <param name="databaseName">The target database name.</param>
    /// <param name="model">Builds the entity model.</param>
    /// <param name="lifetime">The unit-of-work and repository lifetime (scoped by default).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddMongoRepositories(this IServiceCollection services, string connectionString,
        string databaseName, Action<MongoModelBuilder> model, ServiceLifetime lifetime = ServiceLifetime.Scoped) =>
        services.AddMongoDatabase(connectionString, databaseName, model).AddMongoRepositories(lifetime);

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
            scanned,
            sp.GetService(typeof(MigrationSource)) as MigrationSource));

        return services;
    }

    /// <summary>
    ///     Registers the migration runner over <b>explicitly named</b> migrations — the trim/NativeAOT-safe
    ///     form, since nothing is discovered by reflection:
    ///     <code>services.AddMongoMigrations(source => source.Add&lt;ProductsSetup&gt;());</code>
    ///     Ordering still comes from each migration's <c>[Migration]</c> timestamp.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="migrations">Registers the migrations to run.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddMongoMigrations(this IServiceCollection services,
        Action<MigrationSource> migrations)
    {
        var source = new MigrationSource();
        migrations(source);
        services.TryAddSingleton(source);

        services.TryAddScoped<IMigrationExecutor>(sp => new MongoMigrationExecutor(sp.GetRequiredService<IMongoDatabase>()));
        services.TryAddScoped<IMigrationHistory>(sp => new MongoMigrationHistory(sp.GetRequiredService<IMongoDatabase>()));
        services.TryAddScoped<IMigrationRunner>(sp => new MongoMigrationRunner(
            sp.GetRequiredService<IMigrationExecutor>(), sp.GetRequiredService<IMigrationHistory>(),
            [], sp.GetRequiredService<MigrationSource>()));

        return services;
    }
}
