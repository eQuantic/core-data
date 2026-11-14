using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Repository.Locking;

namespace eQuantic.Core.Data.PostgreSql;

/// <summary>
///     Named locks on PostgreSQL: advisory locks on one <c>bigint</c> key, the first eight bytes of the SHA-256 digest
///     of the name's UTF-8 bytes read big-endian (<see cref="NamedLockKeys.Int64" />), so every process and every
///     version derives the same key. Inside a transaction <c>pg_try_advisory_xact_lock</c>, which the server releases
///     when the transaction commits or rolls back; outside one <c>pg_try_advisory_lock</c> for the pinned session,
///     released by <c>pg_advisory_unlock</c> (or by the server when the session ends). The try-forms answer at once, so
///     a wait is a poll: a failed attempt never aborts the caller's transaction, which a <c>lock_timeout</c> would.
///     <para>
///         The single-<c>bigint</c> key space is apart from the two-<c>int</c> one
///         (<c>pg_advisory_lock(int, int)</c>): a lock taken here does not exclude one taken by hand with two keys.
///     </para>
/// </summary>
public sealed class PostgreSqlNamedLocks : SqlNamedLocks
{
    /// <summary>The shared instance.</summary>
    public static PostgreSqlNamedLocks Instance { get; } = new();

    /// <inheritdoc />
    public override bool WaitsOnServer => false;

    /// <inheritdoc />
    public override bool ReleasedWithTransaction => true;

    /// <inheritdoc />
    public override SqlLockStatement Acquire(string name, bool inTransaction, TimeSpan wait) =>
        new(inTransaction ? "SELECT pg_try_advisory_xact_lock(@p0)" : "SELECT pg_try_advisory_lock(@p0)",
            [NamedLockKeys.Int64(name)]);

    /// <inheritdoc />
    public override bool Acquired(object? answer, string name) => answer switch
    {
        bool taken => taken,
        _ => throw new InvalidOperationException(
            $"PostgreSQL answered '{answer ?? "NULL"}' when asked for the named lock '{name}'; a try-lock answers true or false."),
    };

    /// <inheritdoc />
    public override SqlLockStatement Release(string name, bool inTransaction) =>
        new("SELECT pg_advisory_unlock(@p0)", [NamedLockKeys.Int64(name)]);
}
