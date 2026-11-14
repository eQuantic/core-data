using System;

namespace eQuantic.Core.Data.Repository;

/// <summary>
///     Tunes named locks (<see cref="ILockingUnitOfWork.AcquireLockAsync" />). Register one instance as a singleton
///     (<c>services.AddSingleton(new NamedLockOptions { LeaseDuration = TimeSpan.FromSeconds(15) })</c>); without
///     one, the defaults below apply.
/// </summary>
public sealed class NamedLockOptions
{
    private TimeSpan _leaseDuration = TimeSpan.FromSeconds(30);
    private TimeSpan? _renewalInterval;
    private TimeSpan _pollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    ///     How long a lease lasts without being renewed, on the stores that hold a named lock as a lease (MongoDB,
    ///     Cosmos DB, Cassandra): the longest a holder that dies keeps the others waiting. 30 seconds by default; at
    ///     least one second (Cassandra's TTL and Cosmos DB's timestamps count whole seconds). A holder counts it from
    ///     when it <b>sent</b> the write that took or last renewed the lease, and is told it lost the lock
    ///     (<see cref="INamedLock.Lost" />) a tenth of the lease before that term ends without a confirmed renewal.
    /// </summary>
    public TimeSpan LeaseDuration
    {
        get => _leaseDuration;
        set
        {
            if (value < TimeSpan.FromSeconds(1))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "A lease lasts at least one second.");
            }

            _leaseDuration = value;
        }
    }

    /// <summary>
    ///     How often a held lease is renewed: a third of <see cref="LeaseDuration" /> by default, so a renewal can fail
    ///     and be asked again before the lease runs out. It must be shorter than nine tenths of the lease, the moment a
    ///     holder whose renewals were not confirmed is told it lost the lock.
    /// </summary>
    public TimeSpan RenewalInterval
    {
        get => _renewalInterval ?? TimeSpan.FromTicks(_leaseDuration.Ticks / 3);
        set
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "A renewal interval is positive.");
            }

            _renewalInterval = value;
        }
    }

    /// <summary>
    ///     The longest pause between two attempts while waiting on PostgreSQL's try-locks: 100 milliseconds by default.
    ///     Attempts start sooner and back off up to it. A holder waiting for a lease (MongoDB, Cosmos DB, Cassandra),
    ///     each of whose attempts is a write, backs off up to ten times this. The stores that wait on the server
    ///     (SQL Server, MySQL, MariaDB) wait in slices of this length.
    /// </summary>
    public TimeSpan PollInterval
    {
        get => _pollInterval;
        set
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "A poll interval is positive.");
            }

            _pollInterval = value;
        }
    }

    /// <summary>
    ///     Checks the combination: the renewal must come before a holder is told it lost the lock, nine tenths of the
    ///     lease after its last confirmed write.
    /// </summary>
    /// <exception cref="InvalidOperationException">The renewal interval is not shorter than nine tenths of the lease.</exception>
    public void Validate()
    {
        if (RenewalInterval >= LeaseDuration - LostMargin(LeaseDuration))
        {
            throw new InvalidOperationException(
                $"A lease of {LeaseDuration} renewed every {RenewalInterval} is lost before its renewal; renew more often than nine tenths of the lease.");
        }
    }

    /// <summary>
    ///     How long before a lease's term ends its holder is told it lost the lock: a tenth of the lease, for the
    ///     lateness of a timer and the drift of a clock.
    /// </summary>
    /// <param name="leaseDuration">The lease's duration.</param>
    public static TimeSpan LostMargin(TimeSpan leaseDuration) => leaseDuration / 10;
}
