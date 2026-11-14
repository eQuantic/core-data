using System;
using System.Threading;
using System.Threading.Tasks;

namespace eQuantic.Core.Data.Repository.Locking;

/// <summary>
///     The lock a store holds for one name on behalf of one unit of work, taken once however many times the unit of
///     work acquired the name, and released once, when the last of those acquisitions ends.
///     <para>
///         <b>Provider SPI</b>: a provider answers one from <see cref="NamedLockAttempt.TryAsync" />.
///     </para>
/// </summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public abstract class NativeLock
{
    /// <summary>
    ///     Whether the store itself releases the lock when the transaction it was taken in ends (PostgreSQL's
    ///     transaction-level advisory lock, SQL Server's transaction-owned application lock). Such a lock is forgotten
    ///     at the transaction's end, and <see cref="ReleaseAsync" /> has nothing left to do.
    /// </summary>
    public virtual bool EndsWithTransaction => false;

    /// <summary>Signalled when the store stops guaranteeing the lock (a lease that could not be renewed in time).</summary>
    public virtual CancellationToken Lost => CancellationToken.None;

    /// <summary>Releases the lock. Called once; it may throw when the store cannot be reached.</summary>
    public abstract Task ReleaseAsync();
}

/// <summary>
///     One acquisition of a name, from its first attempt to the lock or to giving up: a provider answers it with
///     whatever its attempts share (a connection pinned for a session lock), and it is disposed when the acquisition
///     ends either way.
///     <para>
///         <b>Provider SPI</b>: <see cref="NamedLockSet" /> calls <see cref="TryAsync" /> until it answers a lock, the
///         timeout passes or the token is cancelled.
///     </para>
/// </summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public abstract class NamedLockAttempt : IAsyncDisposable
{
    /// <summary>
    ///     Whether an attempt waits on the store for up to its wait (SQL Server's <c>sp_getapplock</c>, MySQL's
    ///     <c>GET_LOCK</c>), or answers at once (PostgreSQL's try-locks, the leases) and is polled.
    /// </summary>
    public abstract bool WaitsOnStore { get; }

    /// <summary>
    ///     Tries to take the lock once, waiting at most <paramref name="wait" /> on the store when it can wait.
    ///     Answers the lock when it is taken, and <c>null</c> when another holder has it. It must leave no lock behind
    ///     when it answers <c>null</c> or throws, so the token only cancels what cannot take the lock (opening a
    ///     connection); the statement that takes it runs to its answer.
    /// </summary>
    /// <param name="wait">How long the store may wait (zero for the stores that answer at once).</param>
    /// <param name="cancellationToken">Cancels the preparation of the attempt.</param>
    public abstract Task<NativeLock?> TryAsync(TimeSpan wait, CancellationToken cancellationToken);

    /// <summary>Frees what the attempts shared and the lock did not take over.</summary>
    public virtual ValueTask DisposeAsync() => default;
}
