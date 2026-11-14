using System.Diagnostics;
using eQuantic.Core.Data.Diagnostics;
using eQuantic.Core.Data.MongoDb.Extensions;
using eQuantic.Core.Data.MongoDb.Repository;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Locking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>
///     Named locks against a real MongoDB (a single-node replica set, so transactions run): leases in the
///     <c>_locks</c> collection, written by a conditional upsert on the server's clock, renewed while held, and taken
///     over once a dead holder's lease expires. The suite runs on a two-second lease, so expiry is observable. Each
///     holder is a unit of work in its own scope.
/// </summary>
[TestFixture]
public sealed class MongoNamedLockTests : MongoIntegrationTest
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(2);

    private ServiceProvider? _provider;
    private IMongoDatabase? _database;

    [OneTimeTearDown]
    public void DisposeProvider() => _provider?.Dispose();

    private IMongoDatabase Database => _database ??= MongoTestServer.Client.GetDatabase("locks_" + Guid.NewGuid().ToString("N"));

    private LockHolder Holder()
    {
        if (_provider is null)
        {
            var services = new ServiceCollection();
            services.AddSingleton(MongoTestServer.Client);
            services.AddSingleton(Database);
            services.AddSingleton(new NamedLockOptions { LeaseDuration = Lease });
            services.AddMongoRepositories();
            _provider = services.BuildServiceProvider();
        }

        return new LockHolder(_provider.CreateScope(),
            sp => sp.GetRequiredService<MongoDefaultUnitOfWork>().BeginTransactionAsync(),
            sp => sp.GetRequiredService<MongoDefaultUnitOfWork>().CommitTransactionAsync(),
            sp => sp.GetRequiredService<MongoDefaultUnitOfWork>().RollbackTransactionAsync());
    }

    private IMongoCollection<BsonDocument> Leases => Database.GetCollection<BsonDocument>(MongoNamedLocks.CollectionName);

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
    public async Task A_dead_holder_loses_the_lock_when_its_lease_expires()
    {
        using var holder = Holder();
        var name = NamedLockScenarios.Unique("tenant");

        // A holder that dies: its lease is written exactly as a live holder's, and never renewed or released.
        Assert.That(await MongoNamedLocks.Store(Database).TryTakeAsync(name, "dead-holder", Lease, CancellationToken.None), Is.Not.Null);

        var clock = Stopwatch.StartNew();
        Assert.That(async () => await holder.Acquire(name, TimeSpan.Zero), Throws.InstanceOf<TimeoutException>(),
            "the dead holder's lease is live at first");
        await using var taken = await holder.Acquire(name, TimeSpan.FromSeconds(15));
        Assert.That(clock.Elapsed, Is.GreaterThanOrEqualTo(Lease - TimeSpan.FromMilliseconds(300)),
            "taken over only once the lease ran out");

        var lease = await Leases.Find(new BsonDocument("_id", name)).SingleAsync();
        Assert.That(lease["owner"].AsString, Is.Not.EqualTo("dead-holder"));
    }

    [Test]
    public async Task A_live_holder_renews_its_lease_past_the_lease_duration()
    {
        using var first = Holder();
        using var second = Holder();
        var name = NamedLockScenarios.Unique("tenant");

        var held = await first.Acquire(name, TimeSpan.FromSeconds(5));
        await Task.Delay(Lease + Lease);
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
        await Leases.ReplaceOneAsync(new BsonDocument("_id", name),
            new BsonDocument { { "_id", name }, { "owner", "someone-else" }, { "expiresAt", DateTime.UtcNow.AddMinutes(1) } });

        var lost = new TaskCompletionSource();
        await using (held.Lost.Register(() => lost.TrySetResult()))
        {
            Assert.That(await Task.WhenAny(lost.Task, Task.Delay(Lease + Lease)), Is.SameAs(lost.Task),
                "the failed renewal signals the handle");
        }
    }

    [Test]
    public async Task The_lease_is_a_document_with_the_server_s_expiry_and_a_ttl_index()
    {
        using var holder = Holder();
        var name = NamedLockScenarios.Unique("tenant");

        await using (await holder.Acquire(name, TimeSpan.FromSeconds(5)))
        {
            var lease = await Leases.Find(new BsonDocument("_id", name)).SingleAsync();
            Assert.That(lease["owner"].AsString, Does.Contain(Environment.MachineName));
            Assert.That(lease["expiresAt"].ToUniversalTime(), Is.GreaterThan(DateTime.UtcNow.AddSeconds(-5)));

            var indexes = await (await Leases.Indexes.ListAsync()).ToListAsync();
            var ttl = indexes.Single(index => index["name"] == MongoNamedLocks.ExpiryIndexName);
            Assert.That(ttl["expireAfterSeconds"].ToInt32(), Is.Zero);
        }

        Assert.That(await Leases.CountDocumentsAsync(new BsonDocument("_id", name)), Is.Zero, "released: the lease is deleted");
    }

    [Test]
    public Task A_hung_renewal_is_lost_before_another_holder_takes_the_lock() =>
        LeaseScenarios.AHungRenewalIsLostBeforeAnotherHolderTakesTheLock(MongoNamedLocks.Store(Database), Holder, Lease);

    [Test]
    public Task A_lost_lock_is_taken_afresh_not_reentered() =>
        LeaseScenarios.ALostLockIsTakenAfresh(Holder,
            name => Leases.ReplaceOneAsync(new BsonDocument("_id", name),
                new BsonDocument { { "_id", name }, { "owner", "someone-else" }, { "expiresAt", DateTime.UtcNow.AddMinutes(1) } }),
            name => Leases.DeleteOneAsync(new BsonDocument("_id", name)),
            Lease);

    [Test]
    public Task An_acquisition_recognises_its_own_lease() => LeaseScenarios.AnAcquisitionRecognisesItsOwnLease(MongoNamedLocks.Store(Database));

    [Test]
    public Task A_waiter_backs_off() => LeaseScenarios.AWaiterBacksOff(MongoNamedLocks.Store(Database), Holder, Lease);

    [Test]
    public async Task An_expiry_index_the_ttl_one_conflicts_with_does_not_stop_the_locks()
    {
        // The leases collection of a fresh database already has an index on expiresAt, not a TTL one: creating the
        // TTL index conflicts with it. The sweep is housekeeping; the lease's own expiry is what decides.
        var database = MongoTestServer.Client.GetDatabase("locks_" + Guid.NewGuid().ToString("N"));
        var leases = database.GetCollection<BsonDocument>(MongoNamedLocks.CollectionName);
        await leases.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("expiresAt")));

        var warnings = new Warnings();
        var store = MongoNamedLocks.Store(database, warnings);
        var options = new NamedLockOptions { LeaseDuration = Lease };
        var set = new NamedLockSet("mongodb", options);
        var name = NamedLockScenarios.Unique("tenant");
        var dead = NamedLockScenarios.Unique("dead");
        try
        {
            await using (await set.AcquireAsync(name, TimeSpan.FromSeconds(5), false, () => new LeaseLockAttempt(store, name, options), default))
            {
                Assert.That(await leases.CountDocumentsAsync(new BsonDocument("_id", name)), Is.EqualTo(1));
            }

            Assert.That(warnings.Events, Is.EqualTo(new[] { DataEvents.LeaseSweepFailed.Id }), "the index that could not be created is reported once");

            Assert.That(await store.TryTakeAsync(dead, "dead-holder", Lease, CancellationToken.None), Is.Not.Null);
            var clock = Stopwatch.StartNew();
            await using var taken = await set.AcquireAsync(dead, TimeSpan.FromSeconds(15), false,
                () => new LeaseLockAttempt(store, dead, options), default);
            Assert.That(clock.Elapsed, Is.GreaterThanOrEqualTo(Lease - TimeSpan.FromMilliseconds(300)),
                "a dead holder's lease is taken over once it expires, with no TTL index to sweep it");
        }
        finally
        {
            set.ReleaseAll();
            await MongoTestServer.Client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
        }
    }

    [Test]
    public async Task A_lease_inside_a_transaction_is_visible_to_other_holders_and_gone_after_it()
    {
        using var holder = Holder();
        var name = NamedLockScenarios.Unique("tenant");

        await holder.BeginTransaction();
        await holder.Acquire(name, TimeSpan.FromSeconds(5));
        Assert.That(await Leases.CountDocumentsAsync(new BsonDocument("_id", name)), Is.EqualTo(1),
            "written outside the transaction, so another reader sees it while it is open");

        await holder.CommitTransaction();
        Assert.That(await Leases.CountDocumentsAsync(new BsonDocument("_id", name)), Is.Zero);
    }
}

/// <summary>The ids of the warnings a logger was given.</summary>
internal sealed class Warnings : ILogger
{
    private readonly List<int> _events = [];

    public IReadOnlyList<int> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning)
        {
            lock (_events)
            {
                _events.Add(eventId.Id);
            }
        }
    }
}
