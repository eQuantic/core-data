using System.Diagnostics;
using System.Net;
using System.Text.Json;
using eQuantic.Core.Data.CosmosDb.Extensions;
using eQuantic.Core.Data.CosmosDb.Repository;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Locking;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.CosmosDb.Tests;

/// <summary>
///     Named locks against the Cosmos DB emulator: leases in the <c>_locks</c> container, created only when absent
///     (409 otherwise), taken over once expired by the server's clock with an ETag-guarded replace, renewed while held.
///     The suite runs on a two-second lease, so expiry is observable. Each holder is a unit of work in its own scope.
/// </summary>
[TestFixture]
public sealed class CosmosNamedLockTests : CosmosIntegrationTest
{
    // The well-known emulator account key (the same for every emulator).
    private const string EmulatorKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(2);

    private ServiceProvider? _provider;

    [OneTimeTearDown]
    public void DisposeProvider() => _provider?.Dispose();

    private LockHolder Holder()
    {
        if (_provider is null)
        {
            var shared = CosmosTestServer.Provider;
            var services = new ServiceCollection();
            services.AddSingleton(shared.GetRequiredService<CosmosClient>());
            services.AddSingleton(shared.GetRequiredService<CosmosModel>());
            services.AddSingleton(shared.GetRequiredService<Database>());
            services.AddSingleton(new NamedLockOptions { LeaseDuration = Lease });
            services.AddCosmosRepositories();
            _provider = services.BuildServiceProvider();
        }

        return new LockHolder(_provider.CreateScope(),
            sp => sp.GetRequiredService<CosmosDefaultUnitOfWork>().BeginTransactionAsync(),
            sp => sp.GetRequiredService<CosmosDefaultUnitOfWork>().CommitTransactionAsync(),
            sp => sp.GetRequiredService<CosmosDefaultUnitOfWork>().RollbackTransactionAsync());
    }

    private Container Leases => Database.GetContainer(CosmosNamedLocks.ContainerName);

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
        Assert.That(await CosmosNamedLocks.Store(Database).TryTakeAsync(name, "dead-holder", Lease, CancellationToken.None), Is.Not.Null);

        var clock = Stopwatch.StartNew();
        Assert.That(async () => await holder.Acquire(name, TimeSpan.Zero), Throws.InstanceOf<TimeoutException>(),
            "the dead holder's lease is live at first");
        await using var taken = await holder.Acquire(name, TimeSpan.FromSeconds(20));
        Assert.That(clock.Elapsed, Is.GreaterThanOrEqualTo(Lease - TimeSpan.FromMilliseconds(300)),
            "taken over only once the lease ran out by the server's clock");
        Assert.That((await ReadLease(name))!.Value.GetProperty("owner").GetString(), Is.Not.EqualTo("dead-holder"));
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
        var id = CosmosNamedLocks.ItemId(name);
        using (await Leases.UpsertItemStreamAsync(CosmosNamedLocks.Body(id, name, "someone-else", TimeSpan.FromMinutes(1)),
                   new PartitionKey(id)))
        {
        }

        var lost = new TaskCompletionSource();
        await using (held.Lost.Register(() => lost.TrySetResult()))
        {
            Assert.That(await Task.WhenAny(lost.Task, Task.Delay(Lease + Lease)), Is.SameAs(lost.Task),
                "the failed renewal signals the handle");
        }
    }

    [Test]
    public async Task The_lease_is_an_item_named_by_the_lock_and_deleted_on_release()
    {
        using var holder = Holder();
        var name = NamedLockScenarios.Unique("tenant/with?odd#id");

        await using (await holder.Acquire(name, TimeSpan.FromSeconds(5)))
        {
            var lease = await ReadLease(name);
            Assert.That(lease, Is.Not.Null);
            Assert.That(lease!.Value.GetProperty("id").GetString(), Does.StartWith("~"), "a name an id cannot hold is hashed");
            Assert.That(lease.Value.GetProperty("name").GetString(), Is.EqualTo(name));
            Assert.That(lease.Value.GetProperty("owner").GetString(), Does.Contain(Environment.MachineName));
        }

        Assert.That(await ReadLease(name), Is.Null, "released: the item is deleted");
    }

    [Test]
    public Task A_hung_renewal_is_lost_before_another_holder_takes_the_lock() =>
        LeaseScenarios.AHungRenewalIsLostBeforeAnotherHolderTakesTheLock(CosmosNamedLocks.Store(Database), Holder, Lease);

    [Test]
    public Task A_lost_lock_is_taken_afresh_not_reentered() =>
        LeaseScenarios.ALostLockIsTakenAfresh(Holder,
            async name =>
            {
                var id = CosmosNamedLocks.ItemId(name);
                using var _ = await Leases.UpsertItemStreamAsync(CosmosNamedLocks.Body(id, name, "someone-else", TimeSpan.FromMinutes(1)),
                    new PartitionKey(id));
            },
            async name =>
            {
                var id = CosmosNamedLocks.ItemId(name);
                using var _ = await Leases.DeleteItemStreamAsync(id, new PartitionKey(id));
            },
            Lease);

    [Test]
    public Task An_acquisition_recognises_its_own_lease() => LeaseScenarios.AnAcquisitionRecognisesItsOwnLease(CosmosNamedLocks.Store(Database));

    [Test]
    public Task A_waiter_backs_off() => LeaseScenarios.AWaiterBacksOff(CosmosNamedLocks.Store(Database), Holder, Lease);

    [TestCase(HttpStatusCode.RequestTimeout)]
    [TestCase(HttpStatusCode.ServiceUnavailable)]
    public async Task A_lease_create_whose_answer_is_lost_is_settled_by_reading_the_lease_back(HttpStatusCode lostAs)
    {
        var creates = new LeaseCreates { FirstAnsweredAs = lostAs };
        using var client = ClientWith(creates);
        var options = new NamedLockOptions { LeaseDuration = Lease };
        var set = new NamedLockSet("cosmosdb", options);
        var store = CosmosNamedLocks.Store(client.GetDatabase(CosmosTestServer.DatabaseName));
        using var other = Holder();
        var name = NamedLockScenarios.Unique("lost-answer");
        try
        {
            await using (await set.AcquireAsync(name, TimeSpan.FromSeconds(5), false, () => new LeaseLockAttempt(store, name, options), default))
            {
                Assert.That((await ReadLease(name))!.Value.GetProperty("owner").GetString(), Does.Contain(Environment.MachineName),
                    "the create landed, and the lease read back is the holder's");
                Assert.That(async () => await other.Acquire(name, TimeSpan.Zero), Throws.InstanceOf<TimeoutException>());
            }

            Assert.That(creates.Tampered, Is.EqualTo(1), "the create's answer was lost");
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
        // Every lease create lands, and its answer is held back for ten seconds. The lease is long, so a lease left
        // behind would keep the other holder out for the rest of the test.
        var creates = new LeaseCreates { HoldAnswers = TimeSpan.FromSeconds(10) };
        using var client = ClientWith(creates);
        var options = new NamedLockOptions { LeaseDuration = TimeSpan.FromSeconds(30) };
        var set = new NamedLockSet("cosmosdb", options);
        var store = CosmosNamedLocks.Store(client.GetDatabase(CosmosTestServer.DatabaseName));
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

            Assert.That(creates.Tampered, Is.EqualTo(2), "both creates landed, and their answers were held back");
        }
        finally
        {
            set.ReleaseAll();
        }
    }

    /// <summary>A client of the emulator whose requests go through <paramref name="handler" />.</summary>
    private static CosmosClient ClientWith(RequestHandler handler) =>
        new(CosmosTestServer.Provider.GetRequiredService<CosmosClient>().Endpoint.ToString(), EmulatorKey, new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            LimitToEndpoint = true,
            RequestTimeout = TimeSpan.FromMinutes(2),
            CustomHandlers = { handler },
        });

    /// <summary>
    ///     Lets every request reach the emulator, and then tampers with the answer to a lease create: the first one
    ///     answered with a status that says nothing of whether it landed (a 408 or a 503), or every one held back.
    /// </summary>
    private sealed class LeaseCreates : RequestHandler
    {
        private int _creates;
        private int _tampered;

        public HttpStatusCode? FirstAnsweredAs { get; init; }

        /// <summary>The lease creates whose answers were tampered with, so a test knows its handler saw them.</summary>
        public int Tampered => Volatile.Read(ref _tampered);

        public TimeSpan HoldAnswers { get; init; }

        public override async Task<ResponseMessage> SendAsync(RequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (!IsLeaseCreate(request))
            {
                return response;
            }

            if (FirstAnsweredAs is { } status && Interlocked.Increment(ref _creates) == 1)
            {
                Interlocked.Increment(ref _tampered);
                response.Dispose();
                return new ResponseMessage(status, request, "The answer was lost after the write.");
            }

            if (HoldAnswers > TimeSpan.Zero)
            {
                Interlocked.Increment(ref _tampered);
                await Task.Delay(HoldAnswers, cancellationToken);
            }

            return response;
        }

        // A create is a POST to the container's own address that is neither a query nor an upsert.
        private static bool IsLeaseCreate(RequestMessage request) =>
            request.Method == HttpMethod.Post
            && request.Headers["x-ms-documentdb-isquery"] is null
            && request.Headers["x-ms-documentdb-is-upsert"] is null
            && request.RequestUri.OriginalString.TrimEnd('/').EndsWith($"colls/{CosmosNamedLocks.ContainerName}", StringComparison.Ordinal);
    }

    private async Task<JsonElement?> ReadLease(string name)
    {
        var id = CosmosNamedLocks.ItemId(name);
        using var read = await Leases.ReadItemStreamAsync(id, new PartitionKey(id));
        if (read.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        read.EnsureSuccessStatusCode();
        using var item = await JsonDocument.ParseAsync(read.Content);
        return item.RootElement.Clone();
    }
}
