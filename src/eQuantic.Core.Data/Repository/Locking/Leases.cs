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
    ///     Writes the lease of <paramref name="name" /> for <paramref name="owner" /> when there is none, takes it over
    ///     when the one there has expired, and writes it again when the one there is already this owner's (a write of
    ///     the same acquisition that landed although its answer was lost). Every write is conditional on the store, so
    ///     two owners never both succeed.
    ///     <para>
    ///         Answers the lease only when a write of <b>this call</b> made it this owner's, so that its term counts
    ///         from a moment after the call began; <c>null</c> when another owner's lease is live (or it is gone). A
    ///         write whose answer is lost (a timeout that says nothing of whether it landed) is settled by reading the
    ///         lease back: this owner's is written again and answered, anything else answers <c>null</c>. When even
    ///         that cannot tell, the call throws, and the acquisition deletes this owner's lease when it gives up
    ///         (<see cref="ReleaseAsync" />).
    ///     </para>
    /// </summary>
    /// <param name="name">The lock's name.</param>
    /// <param name="owner">The owner's token, the same for every attempt of one acquisition.</param>
    /// <param name="duration">How long the lease lasts unless renewed.</param>
    /// <param name="cancellationToken">
    ///     Signalled at the acquisition's deadline or by its caller: a write cut off by it may or may not have landed,
    ///     which the acquisition settles when it gives up.
    /// </param>
    public abstract Task<Lease?> TryTakeAsync(string name, string owner, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>
    ///     Deletes the lease of <paramref name="name" /> when it is <paramref name="owner" />'s, and leaves anyone
    ///     else's alone: what an acquisition that gives up does about a write of its own it could not settle.
    /// </summary>
    /// <param name="name">The lock's name.</param>
    /// <param name="owner">The owner's token.</param>
    /// <param name="cancellationToken">Bounds the wait for the store.</param>
    public abstract Task ReleaseAsync(string name, string owner, CancellationToken cancellationToken);
}

/// <summary>A lease this process holds (see <see cref="LeaseStore" />).</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public abstract class Lease
{
    /// <summary>
    ///     Pushes the expiry out by <paramref name="duration" />, on the condition that the lease is still this owner's.
    ///     Answers <c>false</c> when it is not (it expired and another owner took it, or it is gone). The engine counts
    ///     the new term from the moment it called, never from the answer.
    /// </summary>
    /// <param name="duration">How long the lease lasts from now.</param>
    /// <param name="cancellationToken">
    ///     Signalled when the renewal can no longer arrive in time (the lease's guaranteed term is over) or the lease
    ///     is released. The engine stops waiting at that moment whether the call honours it or not.
    /// </param>
    public abstract Task<bool> RenewAsync(TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>Deletes the lease, on the condition that it is still this owner's (another owner's is left alone).</summary>
    /// <param name="cancellationToken">Bounds the wait for the store; a lease not deleted runs out on its own.</param>
    public abstract Task ReleaseAsync(CancellationToken cancellationToken);
}

/// <summary>
///     An acquisition of a named lock held as a lease. Every attempt writes as one owner, the acquisition's, so an
///     attempt finds a lease an earlier attempt of its own left (a write whose answer was lost) and keeps it instead of
///     waiting it out. Each attempt is bounded by what is left of the timeout (and given at least
///     <see cref="LeastCallTime" />, so that <see cref="TimeSpan.Zero" /> still makes one whole attempt) and by the
///     caller's token; an acquisition that gives up after a write it could not settle deletes the lease that write may
///     have left. The lease taken is renewed in the background until it is released.
///     <para>
///         <b>Provider SPI</b>: the lease providers hand one to <see cref="NamedLockSet.AcquireAsync" />.
///     </para>
/// </summary>
/// <param name="store">The store's leases.</param>
/// <param name="name">The lock's name.</param>
/// <param name="options">The options in force (lease duration, renewal and polling).</param>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class LeaseLockAttempt(LeaseStore store, string name, NamedLockOptions options) : NamedLockAttempt
{
    /// <summary>
    ///     The least time a call to the store is given: an attempt with less of its timeout left still gets this much,
    ///     and so does the deletion of a lease an acquisition that gave up may have left.
    /// </summary>
    public static readonly TimeSpan LeastCallTime = TimeSpan.FromSeconds(1);

    private readonly string _owner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private bool _unsettled;
    private bool _taken;

    /// <inheritdoc />
    public override bool WaitsOnStore => false;

    /// <inheritdoc />
    /// <remarks>Ten poll intervals: each attempt is a write (on Cosmos DB, a read and a clock query too).</remarks>
    public override TimeSpan? LongestPause => options.PollInterval * 10;

    /// <inheritdoc />
    public override Task<NativeLock?> TryAsync(TimeSpan wait, CancellationToken cancellationToken) =>
        TryAsync(wait, Timeout.InfiniteTimeSpan, cancellationToken);

    /// <inheritdoc />
    public override async Task<NativeLock?> TryAsync(TimeSpan wait, TimeSpan remaining, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (remaining != Timeout.InfiniteTimeSpan)
        {
            deadline.CancelAfter(remaining > LeastCallTime ? remaining : LeastCallTime);
        }

        // The lease's term counts from here: every write this call makes is sent after this moment.
        var sent = LeaseClock.Now;
        var taking = store.TryTakeAsync(name, _owner, options.LeaseDuration, deadline.Token);
        Lease? lease;
        try
        {
            lease = await taking.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception) when (taking.IsCompletedSuccessfully)
        {
            lease = taking.Result; // Answered as it was cut off: the answer stands.
        }
        catch (Exception)
        {
            // Cut off, or failed before it could tell: a lease of this owner may be there. A later attempt finds it
            // (it writes as the same owner); if none does, DisposeAsync deletes it.
            _unsettled = true;
            LeaseClock.Observe(taking);
            if (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new AttemptTimedOutException(); // The timeout passed: the set reports it.
            }

            throw;
        }

        if (lease is null)
        {
            return null;
        }

        _taken = true;
        return new LeaseLock(lease, options, sent);
    }

    /// <summary>
    ///     When the acquisition gave up after a write it could not settle, deletes the lease that write may have left
    ///     (only this owner's), waiting at most <see cref="LeastCallTime" />: one it cannot delete runs out on its own.
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        if (!_unsettled || _taken)
        {
            return;
        }

        using var budget = new CancellationTokenSource(LeastCallTime);
        var releasing = store.ReleaseAsync(name, _owner, budget.Token);
        try
        {
            await releasing.WaitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            LeaseClock.Observe(releasing);
        }
    }
}

/// <summary>
///     A held lease. Its <b>term</b> is the time the store guarantees it: <see cref="NamedLockOptions.LeaseDuration" />
///     counted from when the write that took it, or the last renewal confirmed, was <b>sent</b> (the store applied it
///     later, so its own expiry comes later still). It is renewed in the background every
///     <see cref="NamedLockOptions.RenewalInterval" />, each renewal bounded by what is left of the term, and
///     <see cref="Lost" /> is signalled a tenth of the lease before the term ends without a confirmed renewal,
///     whatever the store or its driver is still doing. A lost lease is not renewed any more, and its release never
///     waits on a renewal.
/// </summary>
internal sealed class LeaseLock : NativeLock
{
    private static readonly TimeSpan LeastRetryPause = TimeSpan.FromMilliseconds(50);

    private readonly Lease _lease;
    private readonly NamedLockOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _lost = new();
    private readonly object _gate = new();
    private readonly Timer _deadline;
    private readonly Task _renewal;
    private TimeSpan _lostAt;
    private bool _isLost;

    /// <summary>Holds a lease taken by a write sent at <paramref name="sent" /> (on <see cref="LeaseClock" />).</summary>
    public LeaseLock(Lease lease, NamedLockOptions options, TimeSpan sent)
    {
        _lease = lease;
        _options = options;
        _lostAt = LostAt(sent);
        _deadline = new Timer(_ => CheckDeadline(), null, Timeout.Infinite, Timeout.Infinite);
        CheckDeadline();
        _renewal = Task.Run(RenewAsync);
    }

    public override CancellationToken Lost => _lost.Token;

    public override async Task ReleaseAsync()
    {
        _stop.Cancel();
        await _deadline.DisposeAsync().ConfigureAwait(false);

        // Every wait of the renewal loop ends with _stop, a renewal still in flight included: this never waits on it.
        await _renewal.ConfigureAwait(false);
        _stop.Dispose();

        // Deleted only while it is still this owner's, so a lease reported lost (and perhaps taken over) is safe to
        // release too: another owner's is left alone. Waiting past a lease's length is pointless: by then the lease
        // has run out on its own.
        using var budget = new CancellationTokenSource(_options.LeaseDuration);
        var releasing = _lease.ReleaseAsync(budget.Token);
        try
        {
            await releasing.WaitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            LeaseClock.Observe(releasing);
            throw new TimeoutException(
                $"The store did not delete the lease within {_options.LeaseDuration}; it runs out on its own.");
        }
    }

    /// <summary>When <see cref="Lost" /> fires for a term counted from <paramref name="sent" />: a tenth of the lease early.</summary>
    private TimeSpan LostAt(TimeSpan sent) => sent + _options.LeaseDuration - NamedLockOptions.LostMargin(_options.LeaseDuration);

    private void CheckDeadline()
    {
        lock (_gate)
        {
            if (_isLost || _stop.IsCancellationRequested)
            {
                return;
            }

            var left = _lostAt - LeaseClock.Now;
            if (left > TimeSpan.Zero)
            {
                try
                {
                    _deadline.Change(left, Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException)
                {
                    // Released meanwhile: nothing is held any more.
                }

                return;
            }
        }

        MarkLost();
    }

    private void MarkLost()
    {
        lock (_gate)
        {
            if (_isLost)
            {
                return;
            }

            _isLost = true;
        }

        // The token's state changes at once; the callbacks run on the thread pool, never on the timer or the
        // renewal loop, and a callback that throws cannot take either down.
        LeaseClock.Observe(_lost.CancelAsync());
    }

    private async Task RenewAsync()
    {
        var pause = _options.RenewalInterval;
        while (true)
        {
            try
            {
                await Task.Delay(pause, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var sent = LeaseClock.Now;
            TimeSpan left;
            lock (_gate)
            {
                if (_isLost)
                {
                    return;
                }

                left = _lostAt - sent;
            }

            if (left <= TimeSpan.Zero)
            {
                CheckDeadline();
                return;
            }

            // A renewal is useful only while the term lasts: past it, Lost has fired, whatever the store answers.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, _lost.Token);
            deadline.CancelAfter(left);
            var renewing = _lease.RenewAsync(_options.LeaseDuration, deadline.Token);
            bool renewed;
            try
            {
                renewed = await renewing.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_stop.IsCancellationRequested || _lost.IsCancellationRequested)
            {
                LeaseClock.Observe(renewing);
                return;
            }
            catch (Exception)
            {
                // The store did not answer in time, or failed. The term still holds until _lostAt; ask again sooner.
                LeaseClock.Observe(renewing);
                var remaining = _lostAt - LeaseClock.Now;
                pause = Min(_options.RenewalInterval, Max(remaining / 2, LeastRetryPause));
                continue;
            }

            if (!renewed)
            {
                // Expired and taken over, or gone: the store no longer holds it for this owner.
                MarkLost();
                return;
            }

            lock (_gate)
            {
                if (_isLost)
                {
                    return;
                }

                var lostAt = LostAt(sent);
                if (lostAt > _lostAt)
                {
                    _lostAt = lostAt;
                }
            }

            CheckDeadline();
            pause = Max(sent + _options.RenewalInterval - LeaseClock.Now, TimeSpan.Zero);
        }
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;
}

/// <summary>The monotonic clock leases count their terms on, and a sink for the calls the engine stopped waiting for.</summary>
internal static class LeaseClock
{
    private static readonly long Origin = Stopwatch.GetTimestamp();

    /// <summary>The time since the clock's origin; it never jumps with the wall clock.</summary>
    public static TimeSpan Now => Stopwatch.GetElapsedTime(Origin);

    /// <summary>Observes a call left running, so its eventual failure is not reported as unobserved.</summary>
    public static void Observe(Task task) =>
        task.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
