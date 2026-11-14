using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace eQuantic.Core.Data.Repository.Locking;

/// <summary>
///     Where a store without a lock of its own keeps named locks as <b>leases</b>: a document, item or row named by
///     the lock, holding its owner and an expiry, written only when absent and taken over only once expired.
///     <para>
///         <b>Provider SPI</b>: MongoDB, Cosmos DB and Cassandra implement it with their own conditional writes.
///     </para>
/// </summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public abstract class LeaseStore
{
    /// <summary>
    ///     Writes the lease of <paramref name="name" /> for <paramref name="owner" /> when there is none, or takes it
    ///     over when the one there has expired. Answers the lease when it is this owner's, <c>null</c> when another
    ///     owner's lease is still live. The write is conditional on the store, so two owners never both succeed.
    /// </summary>
    /// <param name="name">The lock's name.</param>
    /// <param name="owner">The owner's token, unique to this acquisition.</param>
    /// <param name="duration">How long the lease lasts unless renewed.</param>
    public abstract Task<Lease?> TryTakeAsync(string name, string owner, TimeSpan duration);
}

/// <summary>A lease this process holds (see <see cref="LeaseStore" />).</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public abstract class Lease
{
    /// <summary>
    ///     Pushes the expiry out by <paramref name="duration" />, on the condition that the lease is still this owner's.
    ///     Answers <c>false</c> when it is not (it expired and another owner took it, or it is gone).
    /// </summary>
    /// <param name="duration">How long the lease lasts from now.</param>
    public abstract Task<bool> RenewAsync(TimeSpan duration);

    /// <summary>Deletes the lease, on the condition that it is still this owner's (another owner's is left alone).</summary>
    public abstract Task ReleaseAsync();
}

/// <summary>
///     An acquisition of a named lock held as a lease: each attempt tries to write the lease once (or take an expired
///     one over), and the attempts are polled. The lease taken is renewed in the background every
///     <see cref="NamedLockOptions.RenewalInterval" /> until it is released.
///     <para>
///         <b>Provider SPI</b>: the lease providers hand one to <see cref="NamedLockSet.AcquireAsync" />.
///     </para>
/// </summary>
/// <param name="store">The store's leases.</param>
/// <param name="name">The lock's name.</param>
/// <param name="options">The options in force (lease duration and renewal).</param>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class LeaseLockAttempt(LeaseStore store, string name, NamedLockOptions options) : NamedLockAttempt
{
    /// <inheritdoc />
    public override bool WaitsOnStore => false;

    /// <inheritdoc />
    public override async Task<NativeLock?> TryAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        // The write is not cancelled once sent: a write cut off mid-flight may or may not have landed, and a lease
        // nobody knows it holds would keep the others waiting until it expired.
        var lease = await store.TryTakeAsync(name, NewOwner(), options.LeaseDuration).ConfigureAwait(false);
        return lease is null ? null : new LeaseLock(lease, options);
    }

    /// <summary>A token unique to one acquisition, naming the process that holds it (for whoever reads the lease).</summary>
    private static string NewOwner() => $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
}

/// <summary>A held lease, renewed in the background until it is released, and reported lost when a renewal fails.</summary>
internal sealed class LeaseLock : NativeLock
{
    private readonly Lease _lease;
    private readonly NamedLockOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _lost = new();
    private readonly Task _renewal;

    public LeaseLock(Lease lease, NamedLockOptions options)
    {
        _lease = lease;
        _options = options;
        _renewal = Task.Run(RenewAsync);
    }

    public override CancellationToken Lost => _lost.Token;

    public override async Task ReleaseAsync()
    {
        _stop.Cancel();
        await _renewal.ConfigureAwait(false);
        _stop.Dispose();

        // Deleted only while it is still this owner's, so a lease reported lost (and perhaps taken over) is safe to
        // release too: another owner's is left alone.
        await _lease.ReleaseAsync().ConfigureAwait(false);
    }

    private async Task RenewAsync()
    {
        // Counted from the write that took the lease: past the lease's duration without a successful renewal, the
        // store may already have handed it to someone else.
        var sinceRenewal = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                await Task.Delay(_options.RenewalInterval, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                if (await _lease.RenewAsync(_options.LeaseDuration).ConfigureAwait(false))
                {
                    sinceRenewal.Restart();
                    continue;
                }

                // Expired and taken over, or gone: the store no longer holds it for this owner.
                _lost.Cancel();
                return;
            }
            catch (Exception) when (!_stop.IsCancellationRequested)
            {
                // The store did not answer. The lease still holds until it runs out; the next renewal tries again.
                if (sinceRenewal.Elapsed >= _options.LeaseDuration)
                {
                    _lost.Cancel();
                    return;
                }
            }
            catch (Exception)
            {
                return;
            }
        }
    }
}
