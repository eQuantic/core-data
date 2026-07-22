using System.Data.Common;
using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Relational.Repository;
using Microsoft.Data.SqlClient;

namespace eQuantic.Core.Data.SqlServer.Repository;

/// <summary>
///     The native SQL Server unit of work — the shared relational engine over
///     <see cref="Microsoft.Data.SqlClient" />: staged writes flush as one batched, <b>atomic</b> commit
///     (identity keys read back with <c>OUTPUT INSERTED</c>), explicit transactions span commits, and reads join them.
/// </summary>
public abstract class SqlServerUnitOfWork : RelationalUnitOfWork
{
    /// <summary>Initializes the unit of work.</summary>
    protected SqlServerUnitOfWork(IServiceProvider serviceProvider, DbDataSource dataSource, SqlDialect dialect, RelationalModel model)
        : base(serviceProvider, dataSource, dialect, model)
    {
    }
}

/// <summary>The strongly-typed unit of work a consumer derives for its database.</summary>
public abstract class SqlServerUnitOfWork<TDatabase>(IServiceProvider serviceProvider, DbDataSource dataSource, SqlDialect dialect, RelationalModel model)
    : SqlServerUnitOfWork(serviceProvider, dataSource, dialect, model)
    where TDatabase : class;

/// <summary>The default unit of work registered by the DI extensions.</summary>
public sealed class SqlServerDefaultUnitOfWork(IServiceProvider serviceProvider, DbDataSource dataSource, SqlDialect dialect, RelationalModel model)
    : SqlServerUnitOfWork(serviceProvider, dataSource, dialect, model);

/// <summary>
///     A minimal <see cref="DbDataSource" /> over <see cref="SqlConnection" /> —
///     <c>Microsoft.Data.SqlClient</c> does not ship one; pooling stays with the driver's connection pool.
/// </summary>
public sealed class SqlServerDataSource(string connectionString) : DbDataSource
{
    /// <inheritdoc />
    public override string ConnectionString => connectionString;

    /// <inheritdoc />
    protected override DbConnection CreateDbConnection() => new SqlConnection(connectionString);
}
