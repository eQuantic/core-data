using System.Data.Common;
using System.Reflection;
using eQuantic.Core.Data.MySql.Repository;
using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Relational.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MySqlConnector;

namespace eQuantic.Core.Data.MySql.Extensions;

/// <summary>
///     Registers the native MySQL data services: the pooled <see cref="MySqlDataSource" />, the dialect, the
///     entity model, the unit of work and the generic repositories, and (optionally) the SQL-DDL migration runner.
/// </summary>
public static class MySqlServiceCollectionExtensions
{
    /// <summary>Registers the pooled data source (singleton), the MySQL dialect and the entity model.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The MySQL connection string.</param>
    /// <param name="model">Builds the entity model (tables, keys, column overrides).</param>
    /// <param name="functions">Optional custom function translations (<c>Functions.Map(...)</c>).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddMySqlDatabase(this IServiceCollection services, string connectionString,
        Action<RelationalModelBuilder> model, Action<SqlFunctionRegistry>? functions = null)
    {
        var dialect = new MySqlDialect();
        functions?.Invoke(dialect.Functions);
        services.TryAddSingleton<SqlDialect>(dialect);

        var builder = new RelationalModelBuilder(dialect);
        model(builder);
        services.TryAddSingleton(builder.Build());

        services.TryAddSingleton(_ => new MySqlDataSourceBuilder(connectionString).Build());
        services.TryAddSingleton<DbDataSource>(sp => sp.GetRequiredService<MySqlDataSource>());

        return services;
    }

    /// <summary>
    ///     Registers the pooled data source (singleton), the <b>MariaDB</b> dialect and the entity model. MariaDB
    ///     shares MySQL's syntax and driver but supports <c>INSERT … RETURNING</c>, so database-generated keys
    ///     are read back into the entities on commit. Pair with <see cref="AddMySqlRepositories" />.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The MariaDB connection string.</param>
    /// <param name="model">Builds the entity model (tables, keys, column overrides).</param>
    /// <param name="functions">Optional custom function translations (<c>Functions.Map(...)</c>).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddMariaDbDatabase(this IServiceCollection services, string connectionString,
        Action<RelationalModelBuilder> model, Action<SqlFunctionRegistry>? functions = null)
    {
        var dialect = new MariaDbDialect();
        functions?.Invoke(dialect.Functions);
        services.TryAddSingleton<SqlDialect>(dialect);

        var builder = new RelationalModelBuilder(dialect);
        model(builder);
        services.TryAddSingleton(builder.Build());

        services.TryAddSingleton(_ => new MySqlDataSourceBuilder(connectionString).Build());
        services.TryAddSingleton<DbDataSource>(sp => sp.GetRequiredService<MySqlDataSource>());

        return services;
    }

    /// <summary>Registers the generic repositories over the <see cref="MySqlDefaultUnitOfWork" />.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="lifetime">The unit-of-work and repository lifetime (scoped by default).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddMySqlRepositories(this IServiceCollection services,
        ServiceLifetime lifetime = ServiceLifetime.Scoped) =>
        services.AddRelationalRepositories<MySqlDefaultUnitOfWork>(lifetime);

    /// <summary>Registers the generic repositories over a custom <typeparamref name="TUnitOfWork" />.</summary>
    /// <typeparam name="TUnitOfWork">The unit-of-work implementation.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="lifetime">The unit-of-work and repository lifetime (scoped by default).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddMySqlRepositories<TUnitOfWork>(this IServiceCollection services,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TUnitOfWork : MySqlUnitOfWork =>
        services.AddRelationalRepositories<TUnitOfWork>(lifetime);

    /// <summary>
    ///     Registers the SQL-DDL migration runner. Migrations are discovered in the supplied assemblies (the
    ///     calling assembly when none are given); resolve <c>IMigrationRunner</c> and call <c>RunAsync</c> on startup.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="assemblies">The assemblies scanned for migrations; the calling assembly when empty.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddMySqlMigrations(this IServiceCollection services, params Assembly[] assemblies) =>
        services.AddRelationalMigrations(assemblies.Length > 0 ? assemblies : [Assembly.GetCallingAssembly()]);
}
