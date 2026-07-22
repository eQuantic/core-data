using System.Data.Common;
using System.Reflection;
using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Relational.Extensions;
using eQuantic.Core.Data.SqlServer.Repository;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace eQuantic.Core.Data.SqlServer.Extensions;

/// <summary>
///     Registers the native SQL Server data services: the data source, the dialect, the entity model, the unit of
///     work and the generic repositories, and (optionally) the SQL-DDL migration runner.
/// </summary>
public static class SqlServerServiceCollectionExtensions
{
    /// <summary>Registers the data source (singleton), the SQL Server dialect and the entity model.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The SQL Server connection string.</param>
    /// <param name="model">Builds the entity model (tables, keys, column overrides).</param>
    /// <param name="functions">Optional custom function translations (<c>Functions.Map(...)</c>).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddSqlServerDatabase(this IServiceCollection services, string connectionString,
        Action<RelationalModelBuilder> model, Action<SqlFunctionRegistry>? functions = null)
    {
        var dialect = new SqlServerDialect();
        functions?.Invoke(dialect.Functions);
        services.TryAddSingleton<SqlDialect>(dialect);

        var builder = new RelationalModelBuilder(dialect);
        model(builder);
        services.TryAddSingleton(builder.Build());

        services.TryAddSingleton<DbDataSource>(_ => new SqlServerDataSource(connectionString));

        return services;
    }

    /// <summary>Registers the generic repositories over the <see cref="SqlServerDefaultUnitOfWork" />.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="lifetime">The unit-of-work and repository lifetime (scoped by default).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddSqlServerRepositories(this IServiceCollection services,
        ServiceLifetime lifetime = ServiceLifetime.Scoped) =>
        services.AddRelationalRepositories<SqlServerDefaultUnitOfWork>(lifetime);

    /// <summary>Registers the generic repositories over a custom <typeparamref name="TUnitOfWork" />.</summary>
    /// <typeparam name="TUnitOfWork">The unit-of-work implementation.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="lifetime">The unit-of-work and repository lifetime (scoped by default).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddSqlServerRepositories<TUnitOfWork>(this IServiceCollection services,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TUnitOfWork : SqlServerUnitOfWork =>
        services.AddRelationalRepositories<TUnitOfWork>(lifetime);

    /// <summary>
    ///     Registers the SQL-DDL migration runner. Migrations are discovered in the supplied assemblies (the
    ///     calling assembly when none are given); resolve <c>IMigrationRunner</c> and call <c>RunAsync</c> on startup.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="assemblies">The assemblies scanned for migrations; the calling assembly when empty.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddSqlServerMigrations(this IServiceCollection services, params Assembly[] assemblies) =>
        services.AddRelationalMigrations(assemblies.Length > 0 ? assemblies : [Assembly.GetCallingAssembly()]);
}
