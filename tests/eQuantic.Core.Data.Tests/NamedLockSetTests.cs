using System.Diagnostics;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Locking;

namespace eQuantic.Core.Data.Tests;

/// <summary>
///     A store's exclusive locks, in memory: what every provider's attempt asks its own server, here asked of a
///     dictionary. It counts the locks it grants and releases, so a test can see the store's view, not the handles'.
/// </summary>
internal sealed class FakeLockServer
{
    private readonly object _gate = new();
    private readonly Dictionary<string, object> _holders = new();

    public int Granted { get; private set; }

    public int Released { get; private set; }

    public bool IsHeld(string name)
    {
        lock (_gate)
        {
            return _holders.ContainsKey(name);
        }
    }

    public bool TryTake(string name, object holder)
    {
        lock (_gate)
        {
            if (_holders.ContainsKey(name))
            {
                return false;
            }

            _holders[name] = holder;
            Granted++;
            return true;
        }
    }

    public void Release(string name, object holder)
    {
        lock (_gate)
        {
            if (_holders.TryGetValue(name, out var current) && current == holder)
            {
                _holders.Remove(name);
                Released++;
            }
        }
    }
}

/// <summary>An attempt on the fake server, waiting on it (like <c>sp_getapplock</c>) or answering at once (like a try-lock).</summary>
internal sealed class FakeAttempt(FakeLockServer server, string name, bool waitsOnStore, bool endsWithTransaction = false,
    Action? taken = null, CancellationTokenSource? lost = null)
    : NamedLockAttempt
{
    public int Tries { get; private set; }

    public override bool WaitsOnStore => waitsOnStore;

    public override async Task<NativeLock?> TryAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        Tries++;
        var holder = new object();
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (server.TryTake(name, holder))
            {
                taken?.Invoke();
                return new FakeLock(server, name, holder, endsWithTransaction, lost);
            }

            if (clock.Elapsed >= wait)
            {
                return null;
            }

            await Task.Delay(5, CancellationToken.None);
        }
    }

    private sealed class FakeLock(FakeLockServer server, string name, object holder, bool endsWithTransaction,
        CancellationTokenSource? lost) : NativeLock
    {
        public override bool EndsWithTransaction => endsWithTransaction;

        public override CancellationToken Lost => lost?.Token ?? CancellationToken.None;

        public override Task ReleaseAsync()
        {
            if (!endsWithTransaction)
            {
                server.Release(name, holder);
            }

            return Task.CompletedTask;
        }
    }
}

/// <summary>
///     A lease store in memory whose calls can hang (never answering, and ignoring their token, as a driver may). It
///     records the leases an acquisition that gave up asked it to delete.
/// </summary>
internal sealed class FakeLeaseStore : LeaseStore
{
    private readonly object _gate = new();
    private readonly List<string> _forgotten = [];

    public bool TakesHang { get; init; }

    public bool RenewalsHang { get; init; }

    public bool ReleasesHang { get; init; }

    public int Renewals { get; private set; }

    public IReadOnlyList<string> Forgotten
    {
        get
        {
            lock (_gate)
            {
                return [.. _forgotten];
            }
        }
    }

    public override async Task<Lease?> TryTakeAsync(string name, string owner, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (TakesHang)
        {
            await Task.Delay(Timeout.Infinite, CancellationToken.None);
        }

        return new FakeLease(this);
    }

    public override Task ReleaseAsync(string name, string owner, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _forgotten.Add(owner);
        }

        return Task.CompletedTask;
    }

    private sealed class FakeLease(FakeLeaseStore store) : Lease
    {
        public override async Task<bool> RenewAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            store.Renewals++;
            if (store.RenewalsHang)
            {
                await Task.Delay(Timeout.Infinite, CancellationToken.None);
            }

            return true;
        }

        public override async Task ReleaseAsync(CancellationToken cancellationToken)
        {
            if (store.ReleasesHang)
            {
                await Task.Delay(Timeout.Infinite, CancellationToken.None);
            }
        }
    }
}

/// <summary>
///     Unit tests for the rules every provider shares around its store's own lock — no store: waiting and its
///     timeout, cancellation leaving nothing behind, reentrancy, and the end of a transaction. The providers' live
///     suites prove each store's mechanism under the same rules.
/// </summary>
[TestFixture]
public sealed class NamedLockSetTests
{
    private static NamedLockSet NewSet() => new("fake", new NamedLockOptions { PollInterval = TimeSpan.FromMilliseconds(20) });

    private static Func<NamedLockAttempt> On(FakeLockServer server, string name, bool waitsOnStore = false,
        bool endsWithTransaction = false) =>
        () => new FakeAttempt(server, name, waitsOnStore, endsWithTransaction);

    [Test]
    public async Task A_second_holder_waits_until_the_first_lets_go()
    {
        var server = new FakeLockServer();
        var first = NewSet();
        var second = NewSet();

        var held = await first.AcquireAsync("user:42", TimeSpan.FromSeconds(1), false, On(server, "user:42"), default);
        var clock = Stopwatch.StartNew();
        var waiting = second.AcquireAsync("user:42", TimeSpan.FromSeconds(5), false, On(server, "user:42"), default);
        await Task.Delay(300);
        Assert.That(waiting.IsCompleted, Is.False, "the second holder waits while the first holds the name");

        await held.DisposeAsync();
        await using var taken = await waiting;
        Assert.That(clock.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(280)));
        Assert.That(server.IsHeld("user:42"), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task A_timeout_throws_after_about_its_length(bool waitsOnStore)
    {
        var server = new FakeLockServer();
        await using var held = await NewSet().AcquireAsync("tenant:7", TimeSpan.Zero, false, On(server, "tenant:7"), default);

        var clock = Stopwatch.StartNew();
        Assert.ThrowsAsync<TimeoutException>(() =>
            NewSet().AcquireAsync("tenant:7", TimeSpan.FromMilliseconds(200), false, On(server, "tenant:7", waitsOnStore), default));
        Assert.That(clock.Elapsed, Is.InRange(TimeSpan.FromMilliseconds(190), TimeSpan.FromMilliseconds(600)));
    }

    [Test]
    public async Task A_zero_timeout_tries_once()
    {
        var server = new FakeLockServer();
        await using var held = await NewSet().AcquireAsync("tenant:7", TimeSpan.Zero, false, On(server, "tenant:7"), default);

        FakeAttempt? attempt = null;
        Assert.ThrowsAsync<TimeoutException>(() => NewSet().AcquireAsync("tenant:7", TimeSpan.Zero, false,
            () => attempt = new FakeAttempt(server, "tenant:7", waitsOnStore: false), default));
        Assert.That(attempt!.Tries, Is.EqualTo(1));
    }

    [Test]
    public async Task Different_names_do_not_wait_for_each_other()
    {
        var server = new FakeLockServer();
        await using var one = await NewSet().AcquireAsync("user:1", TimeSpan.Zero, false, On(server, "user:1"), default);
        await using var two = await NewSet().AcquireAsync("user:2", TimeSpan.Zero, false, On(server, "user:2"), default);
        Assert.That(server.Granted, Is.EqualTo(2));
    }

    [Test]
    public async Task A_cancelled_wait_throws_and_leaves_no_lock_behind()
    {
        var server = new FakeLockServer();
        var first = await NewSet().AcquireAsync("item:1", TimeSpan.Zero, false, On(server, "item:1"), default);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var waiting = NewSet().AcquireAsync("item:1", Timeout.InfiniteTimeSpan, false, On(server, "item:1", waitsOnStore: true),
            cancellation.Token);
        Assert.That(async () => await waiting, Throws.InstanceOf<OperationCanceledException>());

        await first.DisposeAsync();
        Assert.That(server.IsHeld("item:1"), Is.False, "the cancelled acquisition holds nothing once the first lets go");
    }

    [Test]
    public async Task A_lock_taken_as_the_token_is_cancelled_is_released_before_the_cancellation_is_reported()
    {
        var server = new FakeLockServer();
        using var cancellation = new CancellationTokenSource();
        Assert.That(async () => await NewSet().AcquireAsync("item:2", TimeSpan.FromSeconds(1), false,
                () => new FakeAttempt(server, "item:2", waitsOnStore: false, taken: cancellation.Cancel), cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());

        Assert.That(server.Granted, Is.EqualTo(1), "the attempt took the lock…");
        Assert.That(server.IsHeld("item:2"), Is.False, "…and gave it back before reporting the cancellation");
    }

    [Test]
    public async Task Reacquiring_a_held_name_is_counted_not_locked_twice()
    {
        var server = new FakeLockServer();
        var set = NewSet();

        var outer = await set.AcquireAsync("item:3", TimeSpan.Zero, false, On(server, "item:3"), default);
        var inner = await set.AcquireAsync("item:3", TimeSpan.Zero, false, On(server, "item:3"), default);
        Assert.That(server.Granted, Is.EqualTo(1), "the second acquisition took no second store lock");

        await inner.DisposeAsync();
        Assert.That(server.IsHeld("item:3"), Is.True, "the outer acquisition still holds it");

        await outer.DisposeAsync();
        await outer.DisposeAsync();
        Assert.That(server.IsHeld("item:3"), Is.False);
        Assert.That(server.Released, Is.EqualTo(1));
    }

    [Test]
    public async Task An_acquisition_inside_a_transaction_ends_with_the_transaction_not_with_its_handle()
    {
        var server = new FakeLockServer();
        var set = NewSet();

        var handle = await set.AcquireAsync("tenant:1", TimeSpan.Zero, true, On(server, "tenant:1"), default);
        Assert.That(handle.HeldUntilTransactionEnds, Is.True);

        await handle.DisposeAsync();
        Assert.That(server.IsHeld("tenant:1"), Is.True, "disposing the handle does not end a transaction's lock");

        await set.TransactionEndedAsync();
        Assert.That(server.IsHeld("tenant:1"), Is.False);
    }

    [Test]
    public async Task A_lock_held_outside_outlives_the_transaction_that_reacquired_it()
    {
        var server = new FakeLockServer();
        var set = NewSet();

        var outside = await set.AcquireAsync("tenant:2", TimeSpan.Zero, false, On(server, "tenant:2"), default);
        await set.AcquireAsync("tenant:2", TimeSpan.Zero, true, On(server, "tenant:2"), default);
        await set.TransactionEndedAsync();
        Assert.That(server.IsHeld("tenant:2"), Is.True, "the handle taken outside the transaction still holds it");

        await outside.DisposeAsync();
        Assert.That(server.IsHeld("tenant:2"), Is.False);
    }

    [Test]
    public async Task A_lock_the_store_ties_to_the_transaction_is_forgotten_when_it_ends()
    {
        var server = new FakeLockServer();
        var set = NewSet();

        await set.AcquireAsync("tenant:3", TimeSpan.Zero, true, On(server, "tenant:3", endsWithTransaction: true), default);
        await set.TransactionEndedAsync();

        // The fake server keeps it (the real one would have let go at the commit): the set must not count it as held.
        var again = await set.AcquireAsync("tenant:4", TimeSpan.Zero, false, On(server, "tenant:4"), default);
        await again.DisposeAsync();
        Assert.That(server.Granted, Is.EqualTo(2));
    }

    [Test]
    public async Task Disposing_the_unit_of_work_releases_everything_and_refuses_more()
    {
        var server = new FakeLockServer();
        var set = NewSet();
        await set.AcquireAsync("a", TimeSpan.Zero, false, On(server, "a"), default);
        await set.AcquireAsync("b", TimeSpan.Zero, true, On(server, "b"), default);

        set.ReleaseAll();
        Assert.That(server.IsHeld("a") || server.IsHeld("b"), Is.False);
        Assert.ThrowsAsync<ObjectDisposedException>(() => set.AcquireAsync("c", TimeSpan.Zero, false, On(server, "c"), default));
    }

    [Test]
    public void An_empty_name_or_a_negative_timeout_is_refused()
    {
        var server = new FakeLockServer();
        Assert.ThrowsAsync<ArgumentException>(() => NewSet().AcquireAsync("", TimeSpan.Zero, false, On(server, ""), default));
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            NewSet().AcquireAsync("x", TimeSpan.FromSeconds(-2), false, On(server, "x"), default));
    }

    [Test]
    public void The_64_bit_key_is_pinned_to_its_derivation()
    {
        // The first eight bytes of SHA-256("tenant:42") = 4b5d4625d71bf87f, big-endian. A change here would let two
        // versions of the library lock the same name under two keys.
        Assert.That(NamedLockKeys.Int64("tenant:42"), Is.EqualTo(5430573854015355007L));
        Assert.That(NamedLockKeys.Int64("user:42"), Is.EqualTo(-1567300791643570846L));
    }

    [Test]
    public void A_text_key_is_the_name_when_plain_and_its_digest_otherwise()
    {
        Assert.That(NamedLockKeys.Text("item:42", 64), Is.EqualTo("item:42"));
        Assert.That(NamedLockKeys.Text("Item:ABC", 64, c => c is < 'A' or > 'Z'),
            Is.EqualTo("~680ca1aa538f56c4fed94b31dff32efcff1c6ca9012262c8b6a3d8f8b3aad29"));
        Assert.That(NamedLockKeys.Text("a b", 255), Does.StartWith("~").And.Length.EqualTo(65));
        Assert.That(NamedLockKeys.Text(new string('x', 65), 64), Does.StartWith("~").And.Length.EqualTo(64));
        Assert.That(NamedLockKeys.Text("~item", 255), Is.Not.EqualTo("~item"), "a name that looks hashed is hashed");
        Assert.That(NamedLockKeys.Text("ação", 255), Does.StartWith("~"));
    }

    [Test]
    public async Task A_lost_lock_is_not_reentered_but_taken_afresh()
    {
        var server = new FakeLockServer();
        var set = NewSet();
        using var lost = new CancellationTokenSource();

        var outer = await set.AcquireAsync("item:5", TimeSpan.Zero, false,
            () => new FakeAttempt(server, "item:5", waitsOnStore: false, lost: lost), default);
        await lost.CancelAsync();

        var fresh = await set.AcquireAsync("item:5", TimeSpan.Zero, false, On(server, "item:5"), default);
        Assert.That(server.Granted, Is.EqualTo(2), "the lost lock was let go, and the name taken again from the store");
        Assert.That(fresh.Lost.IsCancellationRequested, Is.False);

        await outer.DisposeAsync();
        Assert.That(server.IsHeld("item:5"), Is.True, "the lost handle ended with its lock: disposing it leaves the fresh one");

        await fresh.DisposeAsync();
        Assert.That(server.IsHeld("item:5"), Is.False);
    }

    [Test]
    public async Task A_lease_whose_renewal_hangs_is_lost_within_its_term_and_released_without_waiting()
    {
        var lease = TimeSpan.FromSeconds(1);
        var options = new NamedLockOptions { LeaseDuration = lease };
        var store = new FakeLeaseStore { RenewalsHang = true };
        var set = new NamedLockSet("fake", options);

        var clock = Stopwatch.StartNew();
        var held = await set.AcquireAsync("tenant:8", TimeSpan.Zero, false, () => new LeaseLockAttempt(store, "tenant:8", options), default);
        var lost = new TaskCompletionSource<TimeSpan>();
        await using var registration = held.Lost.Register(() => lost.TrySetResult(clock.Elapsed));

        var lostAt = await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(store.Renewals, Is.EqualTo(1), "a renewal was sent, and never answered");
        Assert.That(lostAt, Is.InRange(lease * 0.9 - TimeSpan.FromMilliseconds(50), lease),
            "signalled a tenth of the lease before its term, counted from the write that took it, ends");

        var releasing = Stopwatch.StartNew();
        await held.DisposeAsync();
        Assert.That(releasing.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(500)), "the release does not wait on the hung renewal");
    }

    [Test]
    public async Task Disposing_the_unit_of_work_waits_no_longer_than_a_lease_for_a_store_that_does_not_answer()
    {
        var options = new NamedLockOptions { LeaseDuration = TimeSpan.FromSeconds(1) };
        var store = new FakeLeaseStore { ReleasesHang = true };
        var set = new NamedLockSet("fake", options);
        await set.AcquireAsync("a", TimeSpan.Zero, false, () => new LeaseLockAttempt(store, "a", options), default);
        await set.AcquireAsync("b", TimeSpan.Zero, false, () => new LeaseLockAttempt(store, "b", options), default);

        var clock = Stopwatch.StartNew();
        await Task.Run(set.ReleaseAll).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)),
            "both releases give up together after a lease, which by then has run out on its own");
    }

    [Test]
    public async Task A_lease_attempt_keeps_to_its_timeout_and_deletes_what_its_cut_off_write_may_have_left()
    {
        var options = new NamedLockOptions { LeaseDuration = TimeSpan.FromSeconds(5) };
        var store = new FakeLeaseStore { TakesHang = true };

        var clock = Stopwatch.StartNew();
        Assert.ThrowsAsync<TimeoutException>(() => NewSet().AcquireAsync("tenant:9", TimeSpan.FromMilliseconds(200), false,
            () => new LeaseLockAttempt(store, "tenant:9", options), default));
        Assert.That(clock.Elapsed, Is.InRange(LeaseLockAttempt.LeastCallTime - TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(2)),
            "a short timeout still gives the attempt its least time, and no more");
        Assert.That(store.Forgotten, Has.Count.EqualTo(1), "the lease the cut-off write may have left is deleted");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        clock.Restart();
        Assert.That(async () => await NewSet().AcquireAsync("tenant:10", Timeout.InfiniteTimeSpan, false,
            () => new LeaseLockAttempt(store, "tenant:10", options), cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(1)), "the token cuts the attempt short");
        Assert.That(store.Forgotten, Has.Count.EqualTo(2));
    }

    [Test]
    public void A_renewal_must_come_before_the_lease_runs_out()
    {
        var options = new NamedLockOptions { LeaseDuration = TimeSpan.FromSeconds(2), RenewalInterval = TimeSpan.FromSeconds(3) };
        Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Throws<InvalidOperationException>(new NamedLockOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(10), RenewalInterval = TimeSpan.FromSeconds(9.5),
        }.Validate, "a renewal after nine tenths of the lease comes after the holder was told it lost it");
        Assert.That(new NamedLockOptions { LeaseDuration = TimeSpan.FromSeconds(9) }.RenewalInterval, Is.EqualTo(TimeSpan.FromSeconds(3)));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new NamedLockOptions { LeaseDuration = TimeSpan.FromMilliseconds(500) });
    }
}
