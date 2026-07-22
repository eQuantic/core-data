using System.Data.Common;
using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Relational.Repository;

namespace eQuantic.Core.Data.MySql.Repository;

/// <summary>
///     The native MySQL unit of work — the shared relational engine over a
///     <see cref="MySqlConnector.MySqlDataSource" />: staged writes flush as one batched, <b>atomic</b> commit,
///     explicit transactions span commits, and reads join them.
/// </summary>
public abstract class MySqlUnitOfWork : RelationalUnitOfWork
{
    /// <summary>Initializes the unit of work.</summary>
    protected MySqlUnitOfWork(IServiceProvider serviceProvider, DbDataSource dataSource, SqlDialect dialect, RelationalModel model)
        : base(serviceProvider, dataSource, dialect, model)
    {
    }
}

/// <summary>The strongly-typed unit of work a consumer derives for its database.</summary>
public abstract class MySqlUnitOfWork<TDatabase>(IServiceProvider serviceProvider, DbDataSource dataSource, SqlDialect dialect, RelationalModel model)
    : MySqlUnitOfWork(serviceProvider, dataSource, dialect, model)
    where TDatabase : class;

/// <summary>The default unit of work registered by the DI extensions.</summary>
public sealed class MySqlDefaultUnitOfWork(IServiceProvider serviceProvider, DbDataSource dataSource, SqlDialect dialect, RelationalModel model)
    : MySqlUnitOfWork(serviceProvider, dataSource, dialect, model);
