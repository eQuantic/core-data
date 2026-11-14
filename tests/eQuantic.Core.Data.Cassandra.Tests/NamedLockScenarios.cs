using System.Diagnostics;
using eQuantic.Core.Data.Repository;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>
///     One holder of named locks: a unit of work in a DI scope of its own (so two holders are two units of work, two
///     sessions), with its explicit-transaction calls.
/// </summary>
internal sealed class LockHolder(IServiceScope scope, Func<IServiceProvider, Task> begin, Func<IServiceProvider, Task> commit,
    Func<IServiceProvider, Task> rollback) : IDisposable
{
    public ILockingUnitOfWork Locks => scope.ServiceProvider.GetRequiredService<ILockingUnitOfWork>();

    public Task<INamedLock> Acquire(string name, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Locks.AcquireLockAsync(name, timeout, cancellationToken);

    public Task BeginTransaction() => begin(scope.ServiceProvider);

    public Task CommitTransaction() => commit(scope.ServiceProvider);

    public Task RollbackTransaction() => rollback(scope.ServiceProvider);

    public void Dispose() => scope.Dispose();
}

/// <summary>
///     The named-lock scenarios every engine has to pass, through its own mechanism: the spec's three (two holders, a
///     timeout, different names), the release at a transaction's end and at a handle's disposal, a cancellation that
///     leaves nothing behind, reentrancy, and names that only a store's own rules would confuse.
/// </summary>
internal static class NamedLockScenarios
{
    /// <summary>A name no other test uses.</summary>
    public static string Unique(string name) => $"{name}:{Guid.NewGuid():N}";

    /// <summary>
    ///     Two holders: the first takes <c>user:42</c> inside a transaction it commits after one second; the second
    ///     gets the lock only after that commit, within its timeout.
    /// </summary>
    public static async Task TwoHolders(Func<LockHolder> holders)
    {
        using var first = holders();
        using var second = holders();
        var name = Unique("user:42");

        await first.BeginTransaction();
        await first.Acquire(name, TimeSpan.FromSeconds(5));

        var clock = Stopwatch.StartNew();
        var waiting = second.Acquire(name, TimeSpan.FromSeconds(15));
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.That(waiting.IsCompleted, Is.False, "the second holder waits while the first's transaction holds the name");

        var committing = clock.Elapsed;
        await first.CommitTransaction();

        await using var taken = await waiting;
        Assert.That(clock.Elapsed, Is.GreaterThanOrEqualTo(committing),
            "the second holder got the lock only once the first's transaction committed");
        Assert.That(taken.Name, Is.EqualTo(name));
    }

    /// <summary>A holder keeps <c>tenant:7</c>; another asks with 200 ms and gets a timeout after about that long.</summary>
    public static async Task ATimeout(Func<LockHolder> holders)
    {
        using var first = holders();
        using var second = holders();
        var name = Unique("tenant:7");
        await using var held = await first.Acquire(name, TimeSpan.FromSeconds(5));

        var clock = Stopwatch.StartNew();
        Assert.That(async () => await second.Acquire(name, TimeSpan.FromMilliseconds(200)), Throws.InstanceOf<TimeoutException>());
        Assert.That(clock.Elapsed, Is.InRange(TimeSpan.FromMilliseconds(190), TimeSpan.FromSeconds(3)),
            "the timeout comes after about the 200 ms asked for");
    }

    /// <summary>Two holders take <c>user:1</c> and <c>user:2</c> at the same time: neither waits for the other.</summary>
    public static async Task DifferentNames(Func<LockHolder> holders)
    {
        using var first = holders();
        using var second = holders();
        var suffix = Guid.NewGuid().ToString("N");

        var one = first.Acquire($"user:1:{suffix}", TimeSpan.Zero);
        var two = second.Acquire($"user:2:{suffix}", TimeSpan.Zero);
        await using var heldOne = await one;
        await using var heldTwo = await two;
        Assert.That(heldOne.Name, Is.Not.EqualTo(heldTwo.Name));
    }

    /// <summary>Several holders ask for one free name at the same instant, without waiting: exactly one gets it.</summary>
    public static async Task OneOfManyWins(Func<LockHolder> holders)
    {
        var contenders = Enumerable.Range(0, 6).Select(_ => holders()).ToList();
        try
        {
            var name = Unique("race");
            var attempts = contenders.Select(async holder =>
            {
                try
                {
                    return await holder.Acquire(name, TimeSpan.Zero);
                }
                catch (TimeoutException)
                {
                    return null;
                }
            }).ToList();

            var handles = await Task.WhenAll(attempts);
            Assert.That(handles.Count(handle => handle is not null), Is.EqualTo(1), "one holder at a time");
            foreach (var handle in handles.OfType<INamedLock>())
            {
                await handle.DisposeAsync();
            }
        }
        finally
        {
            contenders.ForEach(holder => holder.Dispose());
        }
    }

    /// <summary>Holders that all wait for one name run their critical sections one after another, never two at once.</summary>
    public static async Task MutualExclusion(Func<LockHolder> holders)
    {
        var contenders = Enumerable.Range(0, 4).Select(_ => holders()).ToList();
        try
        {
            var name = Unique("counter");
            var inside = 0;
            var most = 0;
            var entries = 0;
            await Task.WhenAll(contenders.Select(async holder =>
            {
                for (var round = 0; round < 3; round++)
                {
                    await using (await holder.Acquire(name, TimeSpan.FromSeconds(60)))
                    {
                        var now = Interlocked.Increment(ref inside);
                        InterlockedMax(ref most, now);
                        Interlocked.Increment(ref entries);
                        await Task.Delay(20);
                        Interlocked.Decrement(ref inside);
                    }
                }
            }));

            Assert.That(entries, Is.EqualTo(12));
            Assert.That(most, Is.EqualTo(1), "no two holders were ever inside together");
        }
        finally
        {
            contenders.ForEach(holder => holder.Dispose());
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) &&
               Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    /// <summary>A lock taken inside a transaction is released when the transaction rolls back.</summary>
    public static async Task ReleasedOnRollback(Func<LockHolder> holders)
    {
        using var first = holders();
        using var second = holders();
        var name = Unique("item");

        await first.BeginTransaction();
        var handle = await first.Acquire(name, TimeSpan.FromSeconds(5));
        Assert.That(handle.HeldUntilTransactionEnds, Is.True);
        Assert.That(async () => await second.Acquire(name, TimeSpan.Zero), Throws.InstanceOf<TimeoutException>());

        await first.RollbackTransaction();
        await using var taken = await second.Acquire(name, TimeSpan.FromSeconds(5));
    }

    /// <summary>Inside a transaction the lock outlives its handle: only the transaction's end releases it.</summary>
    public static async Task HeldUntilTheTransactionEnds(Func<LockHolder> holders)
    {
        using var first = holders();
        using var second = holders();
        var name = Unique("item");

        await first.BeginTransaction();
        var handle = await first.Acquire(name, TimeSpan.FromSeconds(5));
        await handle.DisposeAsync();
        Assert.That(async () => await second.Acquire(name, TimeSpan.Zero), Throws.InstanceOf<TimeoutException>(),
            "disposing the handle does not end the transaction's lock");

        await first.CommitTransaction();
        await using var taken = await second.Acquire(name, TimeSpan.FromSeconds(5));
    }

    /// <summary>Outside a transaction the lock is released when its handle is disposed.</summary>
    public static async Task ReleasedOnDispose(Func<LockHolder> holders)
    {
        using var first = holders();
        using var second = holders();
        var name = Unique("item");

        var handle = await first.Acquire(name, TimeSpan.FromSeconds(5));
        Assert.That(handle.HeldUntilTransactionEnds, Is.False);
        Assert.That(async () => await second.Acquire(name, TimeSpan.Zero), Throws.InstanceOf<TimeoutException>());

        await handle.DisposeAsync();
        await using var taken = await second.Acquire(name, TimeSpan.FromSeconds(5));
    }

    /// <summary>A unit of work disposed with a lock still held releases it.</summary>
    public static async Task ReleasedWithTheUnitOfWork(Func<LockHolder> holders)
    {
        using var second = holders();
        var name = Unique("item");

        var first = holders();
        await first.Acquire(name, TimeSpan.FromSeconds(5));
        first.Dispose();

        await using var taken = await second.Acquire(name, TimeSpan.FromSeconds(5));
    }

    /// <summary>A cancelled wait throws, and leaves no lock behind once the holder it waited for lets go.</summary>
    public static async Task CancellationLeavesNoLock(Func<LockHolder> holders)
    {
        using var first = holders();
        using var second = holders();
        using var third = holders();
        var name = Unique("item");

        var held = await first.Acquire(name, TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        Assert.That(async () => await second.Acquire(name, Timeout.InfiniteTimeSpan, cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());

        await held.DisposeAsync();
        await using (await third.Acquire(name, TimeSpan.FromSeconds(5)))
        {
        }

        // The cancelled holder kept nothing either: it takes the name afresh, and lets it go again.
        await using (await second.Acquire(name, TimeSpan.FromSeconds(5)))
        {
        }

        await using var again = await third.Acquire(name, TimeSpan.FromSeconds(5));
    }

    /// <summary>Re-acquiring a held name succeeds at once; the lock goes when the last acquisition does.</summary>
    public static async Task Reentrant(Func<LockHolder> holders)
    {
        using var first = holders();
        using var second = holders();
        var name = Unique("item");

        var outer = await first.Acquire(name, TimeSpan.FromSeconds(5));
        var inner = await first.Acquire(name, TimeSpan.Zero);

        await inner.DisposeAsync();
        Assert.That(async () => await second.Acquire(name, TimeSpan.Zero), Throws.InstanceOf<TimeoutException>(),
            "the outer acquisition still holds the name");

        await outer.DisposeAsync();
        await using var taken = await second.Acquire(name, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    ///     Names a store would confuse by its own rules stay apart: two that differ only by case, and two long ones that
    ///     share their first 255 characters.
    /// </summary>
    public static async Task NamesAreComparedExactly(Func<LockHolder> holders)
    {
        using var first = holders();
        using var second = holders();
        var suffix = Guid.NewGuid().ToString("N");
        var prefix = new string('x', 300);

        await using var upper = await first.Acquire($"Item:{suffix}", TimeSpan.Zero);
        await using var lower = await second.Acquire($"item:{suffix}", TimeSpan.Zero);
        await using var longOne = await first.Acquire($"{prefix}:1:{suffix}", TimeSpan.Zero);
        await using var longTwo = await second.Acquire($"{prefix}:2:{suffix}", TimeSpan.Zero);
        await using var spaced = await first.Acquire($"tenant: ação {suffix}", TimeSpan.Zero);
        Assert.That(async () => await second.Acquire($"{prefix}:1:{suffix}", TimeSpan.Zero), Throws.InstanceOf<TimeoutException>(),
            "a long name is still one lock");
    }

    /// <summary>
    ///     A commit that fails still ends the transaction. Here its session is killed under it: the server lets go of
    ///     the lock with the session, and the commit cannot succeed. Another holder then takes the name, the first no
    ///     longer counts it as held (re-acquiring it waits like anyone else's attempt), and a rollback finds no
    ///     transaction left to undo.
    /// </summary>
    /// <param name="holders">Creates a holder.</param>
    /// <param name="killSession">Kills the session of the first holder's open transaction, from the second holder.</param>
    public static async Task AFailedCommitEndsTheTransactionsLocks(Func<LockHolder> holders,
        Func<LockHolder, LockHolder, Task> killSession)
    {
        using var first = holders();
        using var second = holders();
        var name = Unique("commit");

        await first.BeginTransaction();
        await first.Acquire(name, TimeSpan.FromSeconds(5));
        await killSession(first, second);

        Assert.That(async () => await first.CommitTransaction(), Throws.Exception, "the session is gone, and the commit with it");
        await using (await second.Acquire(name, TimeSpan.FromSeconds(5)))
        {
            Assert.That(async () => await first.Acquire(name, TimeSpan.Zero), Throws.InstanceOf<TimeoutException>(),
                "the failed commit ended the transaction's lock: the first holder does not re-enter it");
        }

        Assert.That(async () => await first.RollbackTransaction(), Throws.Nothing,
            "the failed commit left no transaction behind to roll back");
    }
}
