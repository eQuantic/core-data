using System.Data.Common;
using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Relational.Repository;

namespace eQuantic.Core.Data.PostgreSql.Repository;

/// <summary>
///     The native PostgreSQL unit of work — the shared relational engine over an
///     <see cref="Npgsql.NpgsqlDataSource" />: staged writes flush as one batched, <b>atomic</b> commit
///     (generated keys read back with <c>RETURNING</c>), explicit transactions span commits, and reads join them.
/// </summary>
public abstract class PostgreSqlUnitOfWork : RelationalUnitOfWork
{
    /// <summary>Initializes the unit of work.</summary>
    protected PostgreSqlUnitOfWork(IServiceProvider serviceProvider, DbDataSource dataSource, SqlDialect dialect, RelationalModel model)
        : base(serviceProvider, dataSource, dialect, model)
    {
    }
}

/// <summary>The strongly-typed unit of work a consumer derives for its database.</summary>
public abstract class PostgreSqlUnitOfWork<TDatabase>(IServiceProvider serviceProvider, DbDataSource dataSource, SqlDialect dialect, RelationalModel model)
    : PostgreSqlUnitOfWork(serviceProvider, dataSource, dialect, model)
    where TDatabase : class;

/// <summary>The default unit of work registered by the DI extensions.</summary>
public sealed class PostgreSqlDefaultUnitOfWork(IServiceProvider serviceProvider, DbDataSource dataSource, SqlDialect dialect, RelationalModel model)
    : PostgreSqlUnitOfWork(serviceProvider, dataSource, dialect, model);
