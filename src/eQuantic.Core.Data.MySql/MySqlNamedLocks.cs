using System.Globalization;
using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Repository.Locking;

namespace eQuantic.Core.Data.MySql;

/// <summary>
///     Named locks on MySQL and MariaDB: <c>GET_LOCK(name, seconds)</c>, a lock the <b>session</b> holds, which no
///     transaction releases. Outside a transaction it is taken on a pinned connection and released by
///     <c>RELEASE_LOCK</c> when the handle is disposed; inside one it is taken on the transaction's connection and
///     released by the engine right after the commit or rollback. The server dies with the session either way. It
///     answers 1 when the lock is held, 0 when the wait ran out, and NULL on an error (which throws). MariaDB waits
///     for fractions of a second; MySQL rounds the wait to whole seconds, so it is asked for the whole seconds of each
///     slice (none in a slice shorter than one), and the engine waits out the rest between two asks.
///     <para>
///         The lock is named by the name itself when it is at most 64 characters of printable ASCII without upper-case
///         letters; otherwise by <c>~</c> and its SHA-256 (<see cref="NamedLockKeys.Text" />), cut to 64. MySQL refuses
///         longer names, and compares lock names without regard to case, which would make <c>Item:1</c> and
///         <c>item:1</c> one lock where every other store has two.
///     </para>
/// </summary>
public sealed class MySqlNamedLocks : SqlNamedLocks
{
    private const int MaxNameLength = 64;

    private readonly bool _fractionalWait;

    private MySqlNamedLocks(bool fractionalWait) => _fractionalWait = fractionalWait;

    /// <summary>MySQL's: the wait in whole seconds.</summary>
    public static MySqlNamedLocks Instance { get; } = new(fractionalWait: false);

    /// <summary>MariaDB's: the wait in milliseconds.</summary>
    public static MySqlNamedLocks MariaDb { get; } = new(fractionalWait: true);

    /// <inheritdoc />
    public override bool WaitsOnServer => true;

    /// <inheritdoc />
    public override bool ReleasedWithTransaction => false;

    /// <summary>The name <c>GET_LOCK</c> takes for a lock's name.</summary>
    /// <param name="name">The lock's name.</param>
    public static string LockName(string name) => NamedLockKeys.Text(name, MaxNameLength, character => character is < 'A' or > 'Z');

    /// <inheritdoc />
    public override SqlLockStatement Acquire(string name, bool inTransaction, TimeSpan wait) =>
        new("SELECT GET_LOCK(@p0, @p1)",
            [LockName(name), _fractionalWait ? Math.Round((decimal)wait.TotalSeconds, 3) : Math.Floor((decimal)wait.TotalSeconds)]);

    /// <inheritdoc />
    public override bool Acquired(object? answer, string name) => answer switch
    {
        null => throw new InvalidOperationException(
            $"GET_LOCK answered NULL for the named lock '{name}': the server reported an error (the thread was killed, " +
            "or it ran out of memory) rather than a timeout."),
        _ => Convert.ToInt64(answer, CultureInfo.InvariantCulture) switch
        {
            1 => true,
            0 => false,
            var other => throw new InvalidOperationException($"GET_LOCK answered {other} for the named lock '{name}'."),
        },
    };

    /// <inheritdoc />
    public override SqlLockStatement Release(string name, bool inTransaction) =>
        new("SELECT RELEASE_LOCK(@p0)", [LockName(name)]);
}
