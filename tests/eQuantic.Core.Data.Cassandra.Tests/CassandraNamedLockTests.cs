using System.Diagnostics;
using System.Reflection;
using eQuantic.Core.Data.Cassandra.Repository;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Locking;
using Microsoft.Extensions.DependencyInjection;
using global::Cassandra;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>
///     Named locks against a real Cassandra: leases in the <c>equantic_locks</c> table, taken with
///     <c>INSERT … IF NOT EXISTS USING TTL</c>, renewed and released with lightweight transactions conditioned on the
///     owner, and gone once a dead holder's TTL runs out. The suite runs on a two-second lease, so expiry is
///     observable. Each holder is a unit of work in its own scope.
/// </summary>
[TestFixture]
public sealed class CassandraNamedLockTests : CassandraIntegrationTest
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(2);

    private CassandraTestDatabase? _database;

    [OneTimeTearDown]
    public void DisposeDatabase() => _database?.Dispose();

    private CassandraTestDatabase Database =>
        _database ??= CassandraTestServer.NewDatabase(services => services.AddSingleton(new NamedLockOptions { LeaseDuration = Lease }));

    private LockHolder Holder() => new(Database.NewScope(),
        sp => sp.GetRequiredService<CassandraDefaultUnitOfWork>().BeginTransactionAsync(),
        sp => sp.GetRequiredService<CassandraDefaultUnitOfWork>().CommitTransactionAsync(),
        sp => sp.GetRequiredService<CassandraDefaultUnitOfWork>().RollbackTransactionAsync());

    [Test]
    public Task Two_holders_take_turns() => NamedLockScenarios.TwoHolders(Holder);

    [Test]
    public Task A_timeout_throws_after_about_its_length() => NamedLockScenarios.ATimeout(Holder);

    [Test]
    public Task Different_names_do_not_wait() => NamedLockScenarios.DifferentNames(Holder);

    [Test]
    public Task One_of_many_racing_holders_wins() => NamedLockScenarios.OneOfManyWins(Holder);

    [Test]
    public Task Waiting_holders_never_overlap() => NamedLockScenarios.MutualExclusion(Holder);

    [Test]
    public Task A_rollback_releases_the_lock() => NamedLockScenarios.ReleasedOnRollback(Holder);

    [Test]
    public Task Inside_a_transaction_the_lock_outlives_its_handle() => NamedLockScenarios.HeldUntilTheTransactionEnds(Holder);

    [Test]
    public Task Outside_a_transaction_disposing_the_handle_releases_the_lock() => NamedLockScenarios.ReleasedOnDispose(Holder);

    [Test]
    public Task Disposing_the_unit_of_work_releases_the_lock() => NamedLockScenarios.ReleasedWithTheUnitOfWork(Holder);

    [Test]
    public Task A_cancelled_wait_leaves_no_lock_behind() => NamedLockScenarios.CancellationLeavesNoLock(Holder);

    [Test]
    public Task Reacquiring_a_held_name_is_reentrant() => NamedLockScenarios.Reentrant(Holder);

    [Test]
    public Task Names_are_compared_exactly() => NamedLockScenarios.NamesAreComparedExactly(Holder);

    [Test]
    public void A_leases_TTL_covers_the_second_Cassandra_may_cut_from_it()
    {
        // The expiry is the write's time in whole seconds, truncated, plus the TTL: a row written at 10.9 s with a
        // TTL of 2 is gone at 12 s, 1.1 s later. One second more keeps a lease from expiring before its duration.
        Assert.That(CassandraNamedLocks.Ttl(TimeSpan.FromSeconds(2)), Is.EqualTo(3));
        Assert.That(CassandraNamedLocks.Ttl(TimeSpan.FromMilliseconds(1200)), Is.EqualTo(3), "rounded up first");
        Assert.That(CassandraNamedLocks.Ttl(TimeSpan.FromMilliseconds(100)), Is.EqualTo(2));
    }

    [Test]
    public async Task A_dead_holder_loses_the_lock_when_its_lease_expires()
    {
        using var holder = Holder();
        var name = NamedLockScenarios.Unique("tenant");

        // A holder that dies: its lease is written exactly as a live holder's, and never renewed or released.
        Assert.That(await CassandraNamedLocks.Store(Database.Session, null, false).TryTakeAsync(name, "dead-holder", Lease, CancellationToken.None), Is.Not.Null);

        var clock = Stopwatch.StartNew();
        Assert.That(async () => await holder.Acquire(name, TimeSpan.Zero), Throws.InstanceOf<TimeoutException>(),
            "the dead holder's lease is live at first");
        await using var taken = await holder.Acquire(name, TimeSpan.FromSeconds(20));
        Assert.That(clock.Elapsed, Is.GreaterThanOrEqualTo(Lease - TimeSpan.FromMilliseconds(500)),
            "taken over only once the row's TTL ran out");
        Assert.That((await Row(name))!.GetValue<string>("owner"), Is.Not.EqualTo("dead-holder"));
    }

    [Test]
    public async Task A_live_holder_renews_its_lease_past_the_lease_duration()
    {
        using var first = Holder();
        using var second = Holder();
        var name = NamedLockScenarios.Unique("tenant");

        var held = await first.Acquire(name, TimeSpan.FromSeconds(5));
        await Task.Delay(Lease + Lease + TimeSpan.FromSeconds(1));
        Assert.That(async () => await second.Acquire(name, TimeSpan.Zero), Throws.InstanceOf<TimeoutException>(),
            "renewed every third of the lease, the lock outlives two leases");
        Assert.That(held.Lost.IsCancellationRequested, Is.False);

        await held.DisposeAsync();
        await using var taken = await second.Acquire(name, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task A_lease_taken_from_under_its_holder_signals_lost()
    {
        using var holder = Holder();
        var name = NamedLockScenarios.Unique("tenant");
        await using var held = await holder.Acquire(name, TimeSpan.FromSeconds(5));

        // Another owner's lease in its place (what a takeover after a long pause leaves): the next renewal finds it.
        await Database.Session.ExecuteAsync(new SimpleStatement(
            $"UPDATE {CassandraNamedLocks.TableName} USING TTL 60 SET owner = 'someone-else' WHERE name = ?", name));

        var lost = new TaskCompletionSource();
        await using (held.Lost.Register(() => lost.TrySetResult()))
        {
            Assert.That(await Task.WhenAny(lost.Task, Task.Delay(Lease + Lease)), Is.SameAs(lost.Task),
                "the failed renewal signals the handle");
        }
    }

    [Test]
    public async Task The_lease_is_a_row_with_a_ttl_deleted_on_release()
    {
        using var holder = Holder();
        var name = NamedLockScenarios.Unique("tenant");

        await using (await holder.Acquire(name, TimeSpan.FromSeconds(5)))
        {
            var row = await Row(name);
            Assert.That(row, Is.Not.Null);
            Assert.That(row!.GetValue<string>("owner"), Does.Contain(Environment.MachineName));
            // Written with the lease rounded up plus the second Cassandra may cut from it, and counting down since.
            Assert.That(row.GetValue<int?>("ttl"), Is.InRange((int)Lease.TotalSeconds, CassandraNamedLocks.Ttl(Lease)),
                "the row expires on its own, and not before the lease has run");
        }

        Assert.That(await Row(name), Is.Null, "released: the row is deleted");
    }

    [Test]
    public Task A_hung_renewal_is_lost_before_another_holder_takes_the_lock() =>
        LeaseScenarios.AHungRenewalIsLostBeforeAnotherHolderTakesTheLock(CassandraNamedLocks.Store(Database.Session, null, false), Holder, Lease);

    [Test]
    public Task A_lost_lock_is_taken_afresh_not_reentered() =>
        LeaseScenarios.ALostLockIsTakenAfresh(Holder,
            name => Database.Session.ExecuteAsync(new SimpleStatement(
                $"UPDATE {CassandraNamedLocks.TableName} USING TTL 60 SET owner = 'someone-else' WHERE name = ?", name)),
            name => Database.Session.ExecuteAsync(new SimpleStatement(
                $"DELETE FROM {CassandraNamedLocks.TableName} WHERE name = ?", name)),
            Lease);

    [Test]
    public Task An_acquisition_recognises_its_own_lease() =>
        LeaseScenarios.AnAcquisitionRecognisesItsOwnLease(CassandraNamedLocks.Store(Database.Session, null, false));

    [Test]
    public Task A_waiter_backs_off() => LeaseScenarios.AWaiterBacksOff(CassandraNamedLocks.Store(Database.Session, null, false), Holder, Lease);

    [Test]
    public async Task A_lease_insert_whose_CAS_timed_out_is_settled_by_a_serial_read()
    {
        var store = CassandraNamedLocks.Store(LeaseInserts.Over(Database.Session, out var inserts, timeOutFirst: true), null, false);
        var options = new NamedLockOptions { LeaseDuration = Lease };
        var set = new NamedLockSet("cassandra", options);
        using var other = Holder();
        var name = NamedLockScenarios.Unique("cas");
        try
        {
            await using (await set.AcquireAsync(name, TimeSpan.FromSeconds(5), false, () => new LeaseLockAttempt(store, name, options), default))
            {
                Assert.That((await Row(name))!.GetValue<string>("owner"), Does.Contain(Environment.MachineName),
                    "the insert was applied, and the row read back is the holder's");
                Assert.That(async () => await other.Acquire(name, TimeSpan.Zero), Throws.InstanceOf<TimeoutException>());
            }

            Assert.That(inserts.Tampered, Is.EqualTo(1), "the insert's answer was a CAS write timeout");
            await using var taken = await other.Acquire(name, TimeSpan.Zero);
        }
        finally
        {
            set.ReleaseAll();
        }
    }

    [Test]
    public async Task An_acquisition_keeps_to_its_timeout_and_its_token_however_long_the_store_takes_to_answer()
    {
        // Every lease insert is applied, and its answer is held back for ten seconds. The lease is long, so a lease
        // left behind would keep the other holder out for the rest of the test.
        var store = CassandraNamedLocks.Store(LeaseInserts.Over(Database.Session, out var inserts, hold: TimeSpan.FromSeconds(10)), null, false);
        var options = new NamedLockOptions { LeaseDuration = TimeSpan.FromSeconds(30) };
        var set = new NamedLockSet("cassandra", options);
        using var other = Holder();
        try
        {
            var timedOut = NamedLockScenarios.Unique("slow");
            var clock = Stopwatch.StartNew();
            Assert.That(async () => await set.AcquireAsync(timedOut, TimeSpan.FromMilliseconds(300), false,
                () => new LeaseLockAttempt(store, timedOut, options), default), Throws.InstanceOf<TimeoutException>());
            Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)), "the timeout bounds the acquisition, not the store");
            await using (await other.Acquire(timedOut, TimeSpan.Zero))
            {
            }

            var cancelled = NamedLockScenarios.Unique("slow");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            clock.Restart();
            Assert.That(async () => await set.AcquireAsync(cancelled, Timeout.InfiniteTimeSpan, false,
                () => new LeaseLockAttempt(store, cancelled, options), cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)), "the token cuts the acquisition short");
            await using (await other.Acquire(cancelled, TimeSpan.Zero))
            {
            }

            Assert.That(inserts.Tampered, Is.EqualTo(2), "both inserts were applied, and their answers were held back");
        }
        finally
        {
            set.ReleaseAll();
        }
    }

    private async Task<Row?> Row(string name)
    {
        var statement = new SimpleStatement(
            $"SELECT owner, TTL(owner) AS ttl FROM {CassandraNamedLocks.TableName} WHERE name = ?", name);
        statement.SetConsistencyLevel(ConsistencyLevel.Serial);
        return (await Database.Session.ExecuteAsync(statement)).FirstOrDefault();
    }
}

/// <summary>
///     The cluster's session with the answers to the lease inserts tampered with, after each insert has run on the
///     cluster: the first one answered with the CAS write timeout a coordinator gives when it cannot tell whether the
///     Paxos round committed, or every one held back.
/// </summary>
public class LeaseInserts : DispatchProxy
{
    private ISession _session = null!;
    private bool _timeOutFirst;
    private TimeSpan _hold;
    private int _inserts;
    private int _tampered;

    /// <summary>The lease inserts whose answers were tampered with, so a test knows the session saw them.</summary>
    public int Tampered => Volatile.Read(ref _tampered);

    /// <summary>A session over <paramref name="session" /> that tampers with the lease inserts' answers.</summary>
    public static ISession Over(ISession session, out LeaseInserts inserts, bool timeOutFirst = false, TimeSpan hold = default)
    {
        var proxy = Create<ISession, LeaseInserts>();
        inserts = (LeaseInserts)(object)proxy;
        inserts._session = session;
        inserts._timeOutFirst = timeOutFirst;
        inserts._hold = hold;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        object? result;
        try
        {
            result = method!.Invoke(_session, args);
        }
        catch (TargetInvocationException invocation) when (invocation.InnerException is not null)
        {
            throw invocation.InnerException;
        }

        return method.Name == nameof(ISession.ExecuteAsync) && args is [BoundStatement { PreparedStatement.QueryString: var cql }, ..]
                                                            && cql.StartsWith($"INSERT INTO {CassandraNamedLocks.TableName}")
            ? TamperAsync((Task<RowSet>)result!)
            : result;
    }

    private async Task<RowSet> TamperAsync(Task<RowSet> execution)
    {
        var rows = await execution;
        if (_timeOutFirst && Interlocked.Increment(ref _inserts) == 1)
        {
            Interlocked.Increment(ref _tampered);
            throw new WriteTimeoutException(ConsistencyLevel.Serial, 0, 1, "CAS");
        }

        if (_hold > TimeSpan.Zero)
        {
            Interlocked.Increment(ref _tampered);
            await Task.Delay(_hold);
        }

        return rows;
    }
}
