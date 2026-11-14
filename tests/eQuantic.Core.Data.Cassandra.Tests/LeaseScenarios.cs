using System.Diagnostics;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Locking;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>
///     A provider's real lease store, with the engine's calls made to misbehave as a store can: a renewal that hangs
///     (and ignores its token, as a driver may), or a write that lands while its attempt is told it did not. It counts
///     the attempts to take a lease, and measures the pauses between them.
/// </summary>
internal sealed class MisbehavingLeases(LeaseStore store) : LeaseStore
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<(TimeSpan Start, TimeSpan End)> _calls = [];
    private int _attempts;
    private int _lostAnswers;

    /// <summary>How long each renewal hangs before it reaches the store.</summary>
    public TimeSpan RenewalHang { get; init; }

    /// <summary>How many of the first leases taken are answered as not taken although the write landed.</summary>
    public int LostAnswers { init => _lostAnswers = value; }

    /// <summary>The attempts to take a lease so far.</summary>
    public int Attempts => Volatile.Read(ref _attempts);

    /// <summary>The longest pause between the answer to one attempt and the start of the next.</summary>
    public TimeSpan LongestPause
    {
        get
        {
            lock (_calls)
            {
                return _calls.Zip(_calls.Skip(1), (previous, next) => next.Start - previous.End).DefaultIfEmpty().Max();
            }
        }
    }

    public override async Task<Lease?> TryTakeAsync(string name, string owner, TimeSpan duration, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attempts);
        var start = _clock.Elapsed;
        var lease = await store.TryTakeAsync(name, owner, duration, cancellationToken);
        lock (_calls)
        {
            _calls.Add((start, _clock.Elapsed));
        }

        if (lease is not null && Interlocked.Decrement(ref _lostAnswers) >= 0)
        {
            return null;
        }

        return lease is null ? null : new HangingLease(lease, RenewalHang);
    }

    public override Task ReleaseAsync(string name, string owner, CancellationToken cancellationToken) =>
        store.ReleaseAsync(name, owner, cancellationToken);

    private sealed class HangingLease(Lease lease, TimeSpan hang) : Lease
    {
        public override async Task<bool> RenewAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            // A driver that does not answer, and does not listen to its token either.
            await Task.Delay(hang, CancellationToken.None);
            return await lease.RenewAsync(duration, cancellationToken);
        }

        public override Task ReleaseAsync(CancellationToken cancellationToken) => lease.ReleaseAsync(cancellationToken);
    }
}

/// <summary>
///     The lease scenarios every lease store has to pass, on top of the named-lock ones: what a lease promises when
///     its store is slow, silent or misleading. Each runs against the provider's real store.
/// </summary>
internal static class LeaseScenarios
{
    private static Task<INamedLock> Acquire(NamedLockSet set, LeaseStore leases, NamedLockOptions options, string name,
        TimeSpan timeout, CancellationToken cancellationToken = default) =>
        set.AcquireAsync(name, timeout, false, () => new LeaseLockAttempt(leases, name, options), cancellationToken);

    /// <summary>
    ///     A renewal that hangs past the lease: the handle's <c>Lost</c> fires before the lease can run out on the store,
    ///     so the first holder already knows it lost the lock when another one takes it; and releasing the handle does
    ///     not wait for the hung renewal.
    /// </summary>
    public static async Task AHungRenewalIsLostBeforeAnotherHolderTakesTheLock(LeaseStore store, Func<LockHolder> holders,
        TimeSpan lease)
    {
        var options = new NamedLockOptions { LeaseDuration = lease };
        var leases = new MisbehavingLeases(store) { RenewalHang = TimeSpan.FromSeconds(30) };
        var set = new NamedLockSet("test", options);
        var name = NamedLockScenarios.Unique("hung");
        try
        {
            var clock = Stopwatch.StartNew();
            var held = await Acquire(set, leases, options, name, TimeSpan.FromSeconds(5));
            var lostAt = -1L;
            await using var registration = held.Lost.Register(() => Interlocked.CompareExchange(ref lostAt, clock.Elapsed.Ticks, -1));

            using var second = holders();
            await using var taken = await second.Acquire(name, TimeSpan.FromSeconds(20));
            var takenAt = clock.Elapsed;
            Assert.That(held.Lost.IsCancellationRequested, Is.True,
                "the first holder was told it lost the lock before the second could take it");
            Assert.That(TimeSpan.FromTicks(Interlocked.Read(ref lostAt)), Is.LessThanOrEqualTo(takenAt));

            var releasing = Stopwatch.StartNew();
            await held.DisposeAsync();
            Assert.That(releasing.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)), "the release does not wait for the hung renewal");
        }
        finally
        {
            set.ReleaseAll();
        }
    }

    /// <summary>
    ///     A lock lost while held (another owner's lease put in its place) is not re-entered: re-acquiring the name asks
    ///     the store like any other attempt, and takes it afresh once the other owner lets go. Disposing the lost
    ///     handle then leaves the fresh acquisition alone.
    /// </summary>
    public static async Task ALostLockIsTakenAfresh(Func<LockHolder> holders, Func<string, Task> takeOver,
        Func<string, Task> letGo, TimeSpan lease)
    {
        using var holder = holders();
        using var other = holders();
        var name = NamedLockScenarios.Unique("lost");
        var held = await holder.Acquire(name, TimeSpan.FromSeconds(5));
        await takeOver(name);

        var lost = new TaskCompletionSource();
        await using (held.Lost.Register(() => lost.TrySetResult()))
        {
            Assert.That(await Task.WhenAny(lost.Task, Task.Delay(lease + lease)), Is.SameAs(lost.Task), "the handle learns it lost the lock");
        }

        Assert.That(async () => await holder.Acquire(name, TimeSpan.Zero), Throws.InstanceOf<TimeoutException>(),
            "a lost lock is not held: re-acquiring it asks the store, where another owner holds it");

        await letGo(name);
        await using var again = await holder.Acquire(name, TimeSpan.FromSeconds(5));
        Assert.That(again.Lost.IsCancellationRequested, Is.False, "taken afresh, and held");

        await held.DisposeAsync();
        Assert.That(async () => await other.Acquire(name, TimeSpan.Zero), Throws.InstanceOf<TimeoutException>(),
            "disposing the lost handle leaves the fresh acquisition holding the name");
    }

    /// <summary>
    ///     A lease whose write landed while its attempt was told it had not (what a read that settles a lost answer
    ///     before the write lands answers) is recognised by the acquisition's next attempt, which writes as the same
    ///     owner, instead of being waited out.
    /// </summary>
    public static async Task AnAcquisitionRecognisesItsOwnLease(LeaseStore store)
    {
        var options = new NamedLockOptions { LeaseDuration = TimeSpan.FromSeconds(10) };
        var leases = new MisbehavingLeases(store) { LostAnswers = 1 };
        var set = new NamedLockSet("test", options);
        var name = NamedLockScenarios.Unique("own");
        try
        {
            var clock = Stopwatch.StartNew();
            await using var held = await Acquire(set, leases, options, name, TimeSpan.FromSeconds(3));
            Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)), "taken without waiting for the lease to run out");
            Assert.That(leases.Attempts, Is.EqualTo(2), "the second attempt found the first one's lease, and kept it");
        }
        finally
        {
            set.ReleaseAll();
        }
    }

    /// <summary>
    ///     A holder that waits for a lease kept by another backs off: each attempt is a write (and on Cosmos DB a read
    ///     and a clock query as well), so it does not ask every <c>PollInterval</c> for the whole of its wait. Its
    ///     pauses grow past the poll interval, however long the store takes to answer each attempt.
    /// </summary>
    public static async Task AWaiterBacksOff(LeaseStore store, Func<LockHolder> holders, TimeSpan lease)
    {
        using var holder = holders();
        var name = NamedLockScenarios.Unique("busy");
        await using var held = await holder.Acquire(name, TimeSpan.FromSeconds(5));

        var options = new NamedLockOptions { LeaseDuration = lease };
        var leases = new MisbehavingLeases(store);
        var set = new NamedLockSet("test", options);
        try
        {
            Assert.That(async () => await Acquire(set, leases, options, name, TimeSpan.FromSeconds(5)), Throws.InstanceOf<TimeoutException>());

            // Pauses double from 5 ms up to ten poll intervals (1 s), each cut to between half and all of itself: by the
            // time 5 s have passed, one of 320 ms or more has been waited out, whereas a waiter that asks every poll
            // interval never pauses for more than 100 ms. They leave room for at most 19 attempts in 5 s.
            Assert.That(leases.LongestPause, Is.GreaterThan(options.PollInterval * 2.5), "the waiter backs off past the poll interval");
            Assert.That(leases.Attempts, Is.LessThanOrEqualTo(19), "and so asks the store fewer times");
        }
        finally
        {
            set.ReleaseAll();
        }
    }
}
