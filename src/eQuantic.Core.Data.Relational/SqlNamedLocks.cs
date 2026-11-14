namespace eQuantic.Core.Data.Relational;

/// <summary>
///     How a SQL engine takes and releases a named lock: the statements a dialect answers through
///     <see cref="SqlDialect.NamedLocks" />. The shared engine runs them, inside the unit of work's explicit transaction
///     when one is open, and otherwise on a connection the lock pins until it is released; it counts re-acquisitions,
///     waits, and releases at the transaction's end or the handle's disposal.
/// </summary>
public abstract class SqlNamedLocks
{
    /// <summary>
    ///     Whether an attempt waits on the server for up to the wait it is given (<c>sp_getapplock</c>,
    ///     <c>GET_LOCK</c>), or answers at once (<c>pg_try_advisory_lock</c>) and is polled.
    /// </summary>
    public abstract bool WaitsOnServer { get; }

    /// <summary>
    ///     Whether the server releases a lock taken inside a transaction when the transaction ends (PostgreSQL's
    ///     transaction-level advisory lock, SQL Server's transaction-owned application lock). When it does not
    ///     (MySQL's session-level <c>GET_LOCK</c>), the engine runs <see cref="Release" /> once the transaction is over.
    /// </summary>
    public abstract bool ReleasedWithTransaction { get; }

    /// <summary>The statement that tries to take the lock once; it answers one value, which <see cref="Acquired" /> reads.</summary>
    /// <param name="name">The lock's name (the dialect derives its own key from it).</param>
    /// <param name="inTransaction">Whether it is taken inside the open transaction (otherwise for the session).</param>
    /// <param name="wait">How long the server may wait (zero when <see cref="WaitsOnServer" /> is <c>false</c>).</param>
    public abstract SqlLockStatement Acquire(string name, bool inTransaction, TimeSpan wait);

    /// <summary>
    ///     Reads the answer of <see cref="Acquire" />: <c>true</c> when the lock is held, <c>false</c> when another
    ///     holder has it. Any other answer (an error the server reports as a value) throws.
    /// </summary>
    /// <param name="answer">The value the statement answered.</param>
    /// <param name="name">The lock's name (for the message).</param>
    public abstract bool Acquired(object? answer, string name);

    /// <summary>The statement that releases a lock the session (or, without <see cref="ReleasedWithTransaction" />, the transaction) holds.</summary>
    /// <param name="name">The lock's name.</param>
    /// <param name="inTransaction">Whether it was taken inside a transaction.</param>
    public abstract SqlLockStatement Release(string name, bool inTransaction);
}

/// <summary>One statement of a named lock, with the values bound to its <c>@p0, @p1…</c> placeholders.</summary>
/// <param name="Sql">The SQL text.</param>
/// <param name="Parameters">The values, in placeholder order.</param>
public readonly record struct SqlLockStatement(string Sql, IReadOnlyList<object?> Parameters);
