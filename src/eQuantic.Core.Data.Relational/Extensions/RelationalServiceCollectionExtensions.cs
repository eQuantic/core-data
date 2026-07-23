using System.Data.Common;
using System.Reflection;
using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Relational.Migration;
using eQuantic.Core.Data.Relational.Repository;
using eQuantic.Core.Data.Repository;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace eQuantic.Core.Data.Relational.Extensions;

/// <summary>
///     Registers the shared relational data services — the unit of work and the generic repositories, and
///     (optionally) the SQL-DDL migration runner. A provider package (PostgreSQL, MySQL, SQL Server) registers
///     the <see cref="DbDataSource" />, its <see cref="SqlDialect" /> and the <see cref="RelationalModel" />.
/// </summary>
public static class RelationalServiceCollectionExtensions
{
    /// <summary>Registers the generic repositories over a relational <typeparamref name="TUnitOfWork" />.</summary>
    /// <typeparam name="TUnitOfWork">The unit-of-work implementation.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="lifetime">The unit-of-work and repository lifetime (scoped by default).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddRelationalRepositories<TUnitOfWork>(this IServiceCollection services,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TUnitOfWork : RelationalUnitOfWork
    {
        services.AddRelationalUnitOfWork<TUnitOfWork>(lifetime);

        services.TryAdd(new ServiceDescriptor(typeof(IRepository<,>), typeof(RelationalRepository<,>), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(IQueryableRepository<,>), typeof(RelationalRepository<,>), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(IAsyncRepository<,>), typeof(RelationalRepository<,>), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(IAsyncQueryableRepository<,>), typeof(RelationalRepository<,>), lifetime));

        return services;
    }

    /// <summary>
    ///     Registers only the unit of work (concrete plus the <see cref="IUnitOfWork" />/
    ///     <see cref="IQueryableUnitOfWork" /> facades) — AOT-safe, all concrete/closed. The base of the
    ///     NativeAOT registration path: this once, then <see cref="AddRelationalRepository{TEntity, TKey}" /> per
    ///     entity, instead of the open-generic <see cref="AddRelationalRepositories{TUnitOfWork}(IServiceCollection, ServiceLifetime)" />.
    /// </summary>
    /// <typeparam name="TUnitOfWork">The unit-of-work implementation.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="lifetime">The lifetime (scoped by default).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddRelationalUnitOfWork<TUnitOfWork>(this IServiceCollection services,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TUnitOfWork : RelationalUnitOfWork
    {
        services.TryAdd(new ServiceDescriptor(typeof(TUnitOfWork), typeof(TUnitOfWork), lifetime));
        return services.AddUnitOfWorkFacades<TUnitOfWork>(lifetime);
    }

    /// <summary>
    ///     Registers the unit of work through an <b>explicit factory</b> — the NativeAOT-safe form, since the
    ///     container never has to reflect over the concrete constructor. Provider convenience methods
    ///     (<c>Add{Provider}UnitOfWork</c>) call this with the concrete <c>new</c>.
    /// </summary>
    /// <typeparam name="TUnitOfWork">The unit-of-work implementation.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="factory">Constructs the unit of work from resolved services.</param>
    /// <param name="lifetime">The lifetime (scoped by default).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddRelationalUnitOfWork<TUnitOfWork>(this IServiceCollection services,
        Func<IServiceProvider, TUnitOfWork> factory, ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TUnitOfWork : RelationalUnitOfWork
    {
        services.TryAdd(new ServiceDescriptor(typeof(TUnitOfWork), factory, lifetime));
        return services.AddUnitOfWorkFacades<TUnitOfWork>(lifetime);
    }

    private static IServiceCollection AddUnitOfWorkFacades<TUnitOfWork>(this IServiceCollection services, ServiceLifetime lifetime)
        where TUnitOfWork : RelationalUnitOfWork
    {
        services.TryAdd(new ServiceDescriptor(typeof(IQueryableUnitOfWork), sp => sp.GetRequiredService<TUnitOfWork>(), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(IUnitOfWork), sp => sp.GetRequiredService<TUnitOfWork>(), lifetime));
        return services;
    }

    /// <summary>
    ///     Registers the repository interfaces for <b>one</b> entity as <b>closed</b> generic services over
    ///     explicit factories — the NativeAOT-friendly registration. The open-generic
    ///     <see cref="AddRelationalRepositories{TUnitOfWork}(IServiceCollection, ServiceLifetime)" /> needs the DI
    ///     container to close <c>RelationalRepository&lt;,&gt;</c> at runtime, which NativeAOT cannot do when the
    ///     key is a value type (<c>Guid</c>, <c>int</c>). This overload names the closed type at the call site, so
    ///     the AOT compiler roots it statically. Call the unit-of-work registration
    ///     (<see cref="AddRelationalRepositories{TUnitOfWork}(IServiceCollection, ServiceLifetime)" />) once, then
    ///     this per entity — or use the provider's <c>Add{Provider}Repository&lt;TEntity, TKey&gt;()</c> convenience.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="lifetime">The repository lifetime (scoped by default).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddRelationalRepository<TEntity, TKey>(this IServiceCollection services,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TEntity : class, IEntity<TKey>
    {
        static RelationalRepository<TEntity, TKey> Factory(IServiceProvider sp) =>
            new(sp.GetRequiredService<IQueryableUnitOfWork>());

        services.TryAdd(new ServiceDescriptor(typeof(IRepository<TEntity, TKey>), Factory, lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(IQueryableRepository<TEntity, TKey>), Factory, lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(IAsyncRepository<TEntity, TKey>), Factory, lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(IAsyncQueryableRepository<TEntity, TKey>), Factory, lifetime));

        return services;
    }

    /// <summary>
    ///     Opts the relational engine into <b>transient-fault retries</b>: reads retry automatically on
    ///     driver-transient failures (with connection reset and exponential backoff), commits only behind
    ///     <see cref="RelationalResilienceOptions.RetryCommits" />, and nothing retries inside an explicit
    ///     transaction. See <see cref="RelationalResilienceOptions" /> for the honest semantics.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional policy tuning (retries, backoff, commit opt-in).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddRelationalResilience(this IServiceCollection services,
        Action<RelationalResilienceOptions>? configure = null)
    {
        var options = new RelationalResilienceOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        return services;
    }

    /// <summary>
    ///     Registers the SQL-DDL migration runner, executor and history. Migrations are discovered in the supplied
    ///     assemblies (the calling assembly when none are given). Resolve <see cref="IMigrationRunner" /> and call
    ///     <c>RunAsync</c> on startup.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="assemblies">The assemblies scanned for migrations; the calling assembly when empty.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddRelationalMigrations(this IServiceCollection services, params Assembly[] assemblies)
    {
        var scanned = assemblies.Length > 0 ? assemblies : [Assembly.GetCallingAssembly()];

        services.TryAddScoped<IMigrationExecutor>(sp => new RelationalMigrationExecutor(
            sp.GetRequiredService<DbDataSource>(), sp.GetRequiredService<SqlDialect>(), sp.GetRequiredService<RelationalModel>()));
        services.TryAddScoped<IMigrationHistory>(sp => new RelationalMigrationHistory(
            sp.GetRequiredService<DbDataSource>(), sp.GetRequiredService<SqlDialect>()));
        services.TryAddScoped<IMigrationRunner>(sp => new RelationalMigrationRunner(
            sp.GetRequiredService<IMigrationExecutor>(), sp.GetRequiredService<IMigrationHistory>(), scanned));

        return services;
    }
}
