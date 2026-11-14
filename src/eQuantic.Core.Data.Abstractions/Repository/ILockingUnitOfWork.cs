using System;
using System.Threading;
using System.Threading.Tasks;

namespace eQuantic.Core.Data.Repository;

/// <summary>
///     A unit of work that serialises work on a <b>name</b> across processes: a row (<c>item:42</c>), a user, a
///     tenant. Every shipped provider implements it through its store's own mechanism (an advisory lock, an
///     application lock, <c>GET_LOCK</c>, or a lease), with the same observable meaning: one holder at a time per
///     name, names independent of each other.
///     <para>
///         Inside an explicit transaction (<c>BeginTransactionAsync</c> … <c>CommitTransactionAsync</c> or
///         <c>RollbackTransactionAsync</c>) the lock is held until the transaction ends, whether or not the handle is
///         disposed first; outside one, until the handle (or the unit of work) is disposed.
///     </para>
///     <para>
///         Re-acquiring a name this unit of work already holds succeeds at once: the acquisitions are counted, and
///         the store's lock is released when the last of them ends. A unit of work serves one flow at a time, as for
///         every other call; two units of work (two scopes) are two holders.
///     </para>
/// </summary>
public interface ILockingUnitOfWork : IUnitOfWork
{
    /// <summary>
    ///     Waits until no other holder has the lock on <paramref name="name" />, then holds it.
    /// </summary>
    /// <param name="name">The lock's name: any non-empty string, compared exactly (case and all).</param>
    /// <param name="timeout">
    ///     How long to wait for another holder to let go: <see cref="TimeSpan.Zero" /> tries once,
    ///     <see cref="Timeout.InfiniteTimeSpan" /> waits until the lock is free or the token is cancelled.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait; a cancelled acquisition leaves no lock behind.</param>
    /// <returns>The handle that holds the lock; dispose it to release the lock (outside a transaction).</returns>
    /// <exception cref="TimeoutException">Another holder kept the lock for longer than <paramref name="timeout" />.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled before the lock was held.</exception>
    Task<INamedLock> AcquireLockAsync(string name, TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>
///     A named lock held by a unit of work (<see cref="ILockingUnitOfWork.AcquireLockAsync" />). Disposing it
///     releases the lock, unless it was taken inside an explicit transaction: then the transaction's end releases it.
///     Disposing it more than once does nothing.
/// </summary>
public interface INamedLock : IAsyncDisposable
{
    /// <summary>The lock's name, as it was acquired.</summary>
    string Name { get; }

    /// <summary>Whether the lock was taken inside an explicit transaction, and is held until that transaction ends.</summary>
    bool HeldUntilTransactionEnds { get; }

    /// <summary>
    ///     Signalled when the store stops guaranteeing the lock while the handle still holds it: on the stores that
    ///     hold a named lock as a lease (MongoDB, Cosmos DB, Cassandra), a lease that could not be renewed before it
    ///     expired, which another holder may then take. Work that outlives its lease should observe it. The relational
    ///     stores never signal it: their locks last as long as the session or transaction that holds them.
    /// </summary>
    CancellationToken Lost { get; }
}
