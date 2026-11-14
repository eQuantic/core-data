using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Repository.Locking;

namespace eQuantic.Core.Data.SqlServer;

/// <summary>
///     Named locks on SQL Server: an exclusive application lock (<c>sp_getapplock</c>) in the current database, owned by
///     the transaction inside one (the server releases it when the transaction ends) and by the pinned session outside
///     one (released by <c>sp_releaseapplock</c>, or by the server when the session ends). The server waits for up to
///     <c>@LockTimeout</c> milliseconds and answers a result code: 0 or 1 granted, -1 timed out; -3 (chosen as a
///     deadlock victim) and -999 (a parameter or call error) throw. The resource is the name itself when it is short
///     printable ASCII, otherwise <c>~</c> and its SHA-256 (<see cref="NamedLockKeys.Text" />): <c>@Resource</c> holds
///     255 characters and would otherwise cut longer names into one.
/// </summary>
public sealed class SqlServerNamedLocks : SqlNamedLocks
{
    private const int MaxResourceLength = 255;

    /// <summary>The shared instance.</summary>
    public static SqlServerNamedLocks Instance { get; } = new();

    /// <inheritdoc />
    public override bool WaitsOnServer => true;

    /// <inheritdoc />
    public override bool ReleasedWithTransaction => true;

    /// <summary>The application-lock resource a name is locked as.</summary>
    /// <param name="name">The lock's name.</param>
    public static string Resource(string name) => NamedLockKeys.Text(name, MaxResourceLength);

    /// <inheritdoc />
    public override SqlLockStatement Acquire(string name, bool inTransaction, TimeSpan wait) =>
        new($"""
             DECLARE @result int;
             EXEC @result = sp_getapplock @Resource = @p0, @LockMode = 'Exclusive', @LockOwner = '{Owner(inTransaction)}', @LockTimeout = @p1, @DbPrincipal = 'public';
             SELECT @result;
             """,
            [Resource(name), (int)Math.Clamp(Math.Ceiling(wait.TotalMilliseconds), 0, int.MaxValue)]);

    /// <inheritdoc />
    public override bool Acquired(object? answer, string name) => answer switch
    {
        0 or 1 => true,
        -1 => false,
        -3 => throw new InvalidOperationException(
            $"SQL Server chose the request for the named lock '{name}' as a deadlock victim (sp_getapplock answered -3): " +
            "this unit of work holds something the lock's holder is waiting for. Take locks in one order everywhere."),
        _ => throw new InvalidOperationException(
            $"SQL Server refused the named lock '{name}': sp_getapplock answered {answer ?? "NULL"}."),
    };

    /// <inheritdoc />
    public override SqlLockStatement Release(string name, bool inTransaction) =>
        new($"""
             IF APPLOCK_MODE('public', @p0, '{Owner(inTransaction)}') <> 'NoLock'
                 EXEC sp_releaseapplock @Resource = @p0, @LockOwner = '{Owner(inTransaction)}', @DbPrincipal = 'public';
             """,
            [Resource(name)]);

    private static string Owner(bool inTransaction) => inTransaction ? "Transaction" : "Session";
}
