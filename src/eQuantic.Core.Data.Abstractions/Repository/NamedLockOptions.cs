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
    ///     least one second (Cassandra's TTL and Cosmos DB's timestamps count whole seconds).
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
    ///     How often a held lease is renewed: a third of <see cref="LeaseDuration" /> by default, so two renewals can
    ///     fail in a row before the lease runs out. It must be shorter than the lease.
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
    ///     The longest pause between two attempts while waiting, on the stores whose attempt answers at once
    ///     (PostgreSQL's try-locks and the leases): 100 milliseconds by default. Attempts start sooner and back off up
    ///     to it. The stores that wait on the server (SQL Server, MySQL, MariaDB) wait in slices of this length.
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

    /// <summary>Checks the combination: the renewal must come before the lease runs out.</summary>
    /// <exception cref="InvalidOperationException">The renewal interval is not shorter than the lease.</exception>
    public void Validate()
    {
        if (RenewalInterval >= LeaseDuration)
        {
            throw new InvalidOperationException(
                $"A lease of {LeaseDuration} renewed every {RenewalInterval} runs out before its renewal; renew more often than the lease lasts.");
        }
    }
}
