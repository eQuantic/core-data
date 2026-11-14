using eQuantic.Core.Data.PostgreSql.Extensions;
using eQuantic.Core.Data.PostgreSql.Repository;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Relational.Repository;
using eQuantic.Core.Data.Repository.Locking;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace eQuantic.Core.Data.PostgreSql.Tests;

/// <summary>
///     Named locks against a real PostgreSQL: <c>pg_try_advisory_xact_lock</c> inside a transaction (released by the
///     commit or rollback), <c>pg_try_advisory_lock</c> on a pinned session outside one, on the documented 64-bit key.
///     Each holder is a unit of work in its own scope.
/// </summary>
[TestFixture]
public sealed class PostgreSqlNamedLockTests : PostgreSqlIntegrationTest
{
    private ServiceProvider? _provider;

    [OneTimeTearDown]
    public void DisposeProvider() => _provider?.Dispose();

    private LockHolder Holder()
    {
        _provider ??= Services(services => services.AddPostgreSqlRepositories()).BuildServiceProvider();
        return new LockHolder(_provider.CreateScope(),
            sp => sp.GetRequiredService<PostgreSqlDefaultUnitOfWork>().BeginTransactionAsync(),
            sp => sp.GetRequiredService<PostgreSqlDefaultUnitOfWork>().CommitTransactionAsync(),
            sp => sp.GetRequiredService<PostgreSqlDefaultUnitOfWork>().RollbackTransactionAsync());
    }

    private static ServiceCollection Services(Action<IServiceCollection> repositories)
    {
        var services = new ServiceCollection();
        services.AddPostgreSqlDatabase(PostgreSqlTestServer.AdminConnectionString, _ => { });
        repositories(services);
        return services;
    }

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
    public Task A_commit_that_fails_still_ends_the_transaction_and_its_locks() =>
        NamedLockScenarios.AFailedCommitEndsTheTransactionsLocks(Holder, async (victim, killer) =>
        {
            var session = await SessionOf(victim, "SELECT pg_backend_pid()::bigint AS value");
            // Waits (up to 5 s) until the backend is gone, so the commit that follows meets a dead session.
            await ((RelationalUnitOfWork)killer.Locks).ExecuteAsync("SELECT pg_terminate_backend(CAST(@p0 AS integer), 5000)", [session]);
        });

    [Test]
    public async Task The_server_holds_an_advisory_lock_on_the_documented_key()
    {
        using var holder = Holder();
        var name = NamedLockScenarios.Unique("tenant");
        var key = NamedLockKeys.Int64(name);

        await holder.BeginTransaction();
        await holder.Acquire(name, TimeSpan.FromSeconds(5));
        Assert.That(await AdvisoryLocksOn(key), Is.EqualTo(1), "a transaction-level advisory lock on the derived key");

        await holder.CommitTransaction();
        Assert.That(await AdvisoryLocksOn(key), Is.Zero, "the commit released it");

        await using (await holder.Acquire(name, TimeSpan.FromSeconds(5)))
        {
            Assert.That(await AdvisoryLocksOn(key), Is.EqualTo(1), "a session-level advisory lock on a pinned connection");
        }

        Assert.That(await AdvisoryLocksOn(key), Is.Zero, "disposing the handle unlocked it");
    }

    [Test]
    public async Task A_lock_taken_by_hand_on_the_same_key_excludes_the_unit_of_work()
    {
        using var holder = Holder();
        var name = NamedLockScenarios.Unique("tenant");

        await using var connection = new NpgsqlConnection(PostgreSqlTestServer.AdminConnectionString);
        await connection.OpenAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", connection))
        {
            command.Parameters.AddWithValue("key", NamedLockKeys.Int64(name));
            await command.ExecuteNonQueryAsync();
        }

        Assert.That(async () => await holder.Acquire(name, TimeSpan.FromMilliseconds(100)), Throws.InstanceOf<TimeoutException>());

        await using (var command = new NpgsqlCommand("SELECT pg_advisory_unlock_all()", connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        await using var taken = await holder.Acquire(name, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task The_aot_registration_resolves_the_locking_unit_of_work()
    {
        await using var provider = Services(services => services.AddPostgreSqlUnitOfWork()).BuildServiceProvider();
        using var scope = provider.CreateScope();

        var locks = scope.ServiceProvider.GetRequiredService<ILockingUnitOfWork>();
        Assert.That(locks, Is.SameAs(scope.ServiceProvider.GetRequiredService<IUnitOfWork>()));
        await using var held = await locks.AcquireLockAsync(NamedLockScenarios.Unique("aot"), TimeSpan.FromSeconds(5));
    }

    /// <summary>The id of the session a holder's open transaction runs on, as <paramref name="sql" /> answers it.</summary>
    private static async Task<long> SessionOf(LockHolder holder, string sql) =>
        (await ((RelationalUnitOfWork)holder.Locks).QueryAsync<SessionRow>(sql))[0].Value;

    private sealed class SessionRow
    {
        public long Value { get; set; }
    }

    private static async Task<long> AdvisoryLocksOn(long key)
    {
        await using var connection = new NpgsqlConnection(PostgreSqlTestServer.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND objsubid = 1 " +
            "AND ((classid::bigint << 32) | objid::bigint) = @key AND granted", connection);
        command.Parameters.AddWithValue("key", key);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
