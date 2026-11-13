using System.Diagnostics;
using System.Net;
using System.Text.Json;
using eQuantic.Core.Data.CosmosDb.Extensions;
using eQuantic.Core.Data.CosmosDb.Repository;
using eQuantic.Core.Data.Repository;
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
        Assert.That(await CosmosNamedLocks.Store(Database).TryTakeAsync(name, "dead-holder", Lease), Is.Not.Null);

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
