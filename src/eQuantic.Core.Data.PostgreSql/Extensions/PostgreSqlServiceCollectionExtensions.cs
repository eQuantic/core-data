using System.Data.Common;
using System.Reflection;
using eQuantic.Core.Data.PostgreSql.Repository;
using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Relational.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace eQuantic.Core.Data.PostgreSql.Extensions;

/// <summary>
///     Registers the native PostgreSQL data services: the pooled <see cref="NpgsqlDataSource" />, the dialect, the
///     entity model, the unit of work and the generic repositories, and (optionally) the SQL-DDL migration runner.
/// </summary>
public static class PostgreSqlServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the pooled data source (singleton, with automatic statement preparation), the PostgreSQL
    ///     dialect and the entity model.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The PostgreSQL connection string.</param>
    /// <param name="model">Builds the entity model (tables, keys, column overrides).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddPostgreSqlDatabase(this IServiceCollection services, string connectionString,
        Action<RelationalModelBuilder> model)
    {
        var dialect = new PostgreSqlDialect();
        services.TryAddSingleton<SqlDialect>(dialect);

        var builder = new RelationalModelBuilder(dialect);
        model(builder);
        services.TryAddSingleton(builder.Build());

        services.TryAddSingleton(_ =>
        {
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
            if (!connectionString.Contains("Max Auto Prepare", StringComparison.OrdinalIgnoreCase))
            {
                dataSourceBuilder.ConnectionStringBuilder.MaxAutoPrepare = 32;
            }

            return dataSourceBuilder.Build();
        });
        services.TryAddSingleton<DbDataSource>(sp => sp.GetRequiredService<NpgsqlDataSource>());

        return services;
    }

    /// <summary>Registers the generic repositories over the <see cref="PostgreSqlDefaultUnitOfWork" />.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="lifetime">The unit-of-work and repository lifetime (scoped by default).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddPostgreSqlRepositories(this IServiceCollection services,
        ServiceLifetime lifetime = ServiceLifetime.Scoped) =>
        services.AddRelationalRepositories<PostgreSqlDefaultUnitOfWork>(lifetime);

    /// <summary>Registers the generic repositories over a custom <typeparamref name="TUnitOfWork" />.</summary>
    /// <typeparam name="TUnitOfWork">The unit-of-work implementation.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="lifetime">The unit-of-work and repository lifetime (scoped by default).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddPostgreSqlRepositories<TUnitOfWork>(this IServiceCollection services,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TUnitOfWork : PostgreSqlUnitOfWork =>
        services.AddRelationalRepositories<TUnitOfWork>(lifetime);

    /// <summary>
    ///     Registers the SQL-DDL migration runner. Migrations are discovered in the supplied assemblies (the
    ///     calling assembly when none are given); resolve <c>IMigrationRunner</c> and call <c>RunAsync</c> on startup.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="assemblies">The assemblies scanned for migrations; the calling assembly when empty.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddPostgreSqlMigrations(this IServiceCollection services, params Assembly[] assemblies) =>
        services.AddRelationalMigrations(assemblies.Length > 0 ? assemblies : [Assembly.GetCallingAssembly()]);
}
