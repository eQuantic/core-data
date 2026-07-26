using System.Reflection;
using eQuantic.Core.Data.Cassandra.Migration;
using eQuantic.Core.Data.Cassandra.Repository;
using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Repository;
using global::Cassandra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace eQuantic.Core.Data.Cassandra.Extensions;

/// <summary>
///     Registers the native Apache Cassandra data services: the session, the entity model (tables and keys), the
///     unit of work and the generic repositories, and (optionally) the CQL-DDL migration runner.
/// </summary>
public static class CassandraServiceCollectionExtensions
{
    /// <summary>
    ///     Registers an <see cref="ISession" /> (singleton) connected to <paramref name="keyspace" /> — creating the
    ///     keyspace with simple replication if it does not exist.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="keyspace">The keyspace name.</param>
    /// <param name="contactPoints">The cluster contact points.</param>
    /// <param name="port">The native protocol port (9042 by default).</param>
    /// <param name="replicationFactor">The simple-strategy replication factor for a created keyspace.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCassandraSession(this IServiceCollection services, string keyspace,
        string[] contactPoints, int port = 9042, int replicationFactor = 1)
    {
        services.TryAddSingleton<ISession>(_ =>
        {
            var cluster = Cluster.Builder().AddContactPoints(contactPoints).WithPort(port).Build();
            var session = cluster.Connect();
            session.Execute(
                $"CREATE KEYSPACE IF NOT EXISTS {keyspace} WITH replication = {{'class':'SimpleStrategy','replication_factor':{replicationFactor}}}");
            session.ChangeKeyspace(keyspace);
            return session;
        });

        return services;
    }

    /// <summary>Registers the entity model and the generic repositories over the <see cref="CassandraDefaultUnitOfWork" />.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="model">Builds the entity model (tables, partition/clustering keys).</param>
    /// <param name="lifetime">The unit-of-work and repository lifetime (scoped by default).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCassandraRepositories(this IServiceCollection services,
        Action<CassandraModelBuilder> model, ServiceLifetime lifetime = ServiceLifetime.Scoped) =>
        services.AddCassandraRepositories<CassandraDefaultUnitOfWork>(model, lifetime);

    /// <summary>Registers the entity model and the generic repositories over a custom <typeparamref name="TUnitOfWork" />.</summary>
    /// <typeparam name="TUnitOfWork">The unit-of-work implementation.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="model">Builds the entity model.</param>
    /// <param name="lifetime">The unit-of-work and repository lifetime (scoped by default).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCassandraRepositories<TUnitOfWork>(this IServiceCollection services,
        Action<CassandraModelBuilder> model, ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TUnitOfWork : CassandraUnitOfWork
    {
        var builder = new CassandraModelBuilder();
        model(builder);
        services.TryAddSingleton(builder.Build());

        // Describes the model for the tooling that compares one version of it against another.
        services.TryAddSingleton<Data.Evolution.IModelSnapshotSource>(provider =>
            new Evolution.CassandraModelSnapshotSource(provider.GetRequiredService<CassandraModel>()));

        // Reads the keyspace as it actually is, so the model can be checked against reality.
        services.TryAddSingleton<Data.Evolution.IDatabaseSnapshotSource>(provider =>
            new Evolution.CassandraDatabaseSnapshotSource(provider.GetRequiredService<CassandraModel>(),
                provider.GetRequiredService<ISession>()));

        services.TryAdd(new ServiceDescriptor(typeof(TUnitOfWork), typeof(TUnitOfWork), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(IQueryableUnitOfWork), sp => sp.GetRequiredService<TUnitOfWork>(), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(IUnitOfWork), sp => sp.GetRequiredService<TUnitOfWork>(), lifetime));

        services.TryAdd(new ServiceDescriptor(typeof(IRepository<,>), typeof(CassandraRepository<,>), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(IQueryableRepository<,>), typeof(CassandraRepository<,>), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(IAsyncRepository<,>), typeof(CassandraRepository<,>), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(IAsyncQueryableRepository<,>), typeof(CassandraRepository<,>), lifetime));

        return services;
    }

    /// <summary>
    ///     Registers the CQL-DDL migration runner, executor and history. Migrations are discovered in the supplied
    ///     assemblies (the calling assembly when none are given). Resolve <see cref="IMigrationRunner" /> and call
    ///     <c>RunAsync</c> on startup.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="assemblies">The assemblies scanned for migrations; the calling assembly when empty.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCassandraMigrations(this IServiceCollection services, params Assembly[] assemblies)
    {
        var scanned = assemblies.Length > 0 ? assemblies : [Assembly.GetCallingAssembly()];

        services.TryAddScoped<IMigrationExecutor>(sp => new CassandraMigrationExecutor(
            sp.GetRequiredService<ISession>(), sp.GetRequiredService<CassandraModel>()));
        services.TryAddScoped<IMigrationHistory>(sp => new CassandraMigrationHistory(sp.GetRequiredService<ISession>()));
        services.TryAddScoped<IMigrationRunner>(sp => new CassandraMigrationRunner(
            sp.GetRequiredService<IMigrationExecutor>(), sp.GetRequiredService<IMigrationHistory>(), scanned,
            sp.GetService(typeof(MigrationSource)) as MigrationSource));

        return services;
    }

    /// <summary>
    ///     Registers the migration runner over <b>explicitly named</b> migrations — the trim/NativeAOT-safe
    ///     form, since nothing is discovered by reflection:
    ///     <code>services.AddCassandraMigrations(source => source.Add&lt;ProductsSetup&gt;());</code>
    ///     Ordering still comes from each migration's <c>[Migration]</c> timestamp.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="migrations">Registers the migrations to run.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCassandraMigrations(this IServiceCollection services,
        Action<MigrationSource> migrations)
    {
        var source = new MigrationSource();
        migrations(source);
        services.TryAddSingleton(source);

        services.TryAddScoped<IMigrationExecutor>(sp => new CassandraMigrationExecutor(
            sp.GetRequiredService<ISession>(), sp.GetRequiredService<CassandraModel>()));
        services.TryAddScoped<IMigrationHistory>(sp => new CassandraMigrationHistory(sp.GetRequiredService<ISession>()));
        services.TryAddScoped<IMigrationRunner>(sp => new CassandraMigrationRunner(
            sp.GetRequiredService<IMigrationExecutor>(), sp.GetRequiredService<IMigrationHistory>(),
            [], sp.GetRequiredService<MigrationSource>()));

        return services;
    }
}
