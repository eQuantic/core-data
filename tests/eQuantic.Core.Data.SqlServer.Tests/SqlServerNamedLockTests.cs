using eQuantic.Core.Data.Relational.Repository;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.SqlServer.Extensions;
using eQuantic.Core.Data.SqlServer.Repository;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.SqlServer.Tests;

/// <summary>
///     Named locks against a real SQL Server: <c>sp_getapplock</c> owned by the transaction inside one (released by the
///     commit or rollback) and by a pinned session outside one, waiting on the server in slices. Each holder is a unit
///     of work in its own scope.
/// </summary>
[TestFixture]
public sealed class SqlServerNamedLockTests : SqlServerIntegrationTest
{
    private ServiceProvider? _provider;

    [OneTimeTearDown]
    public void DisposeProvider() => _provider?.Dispose();

    private LockHolder Holder()
    {
        if (_provider is null)
        {
            var services = new ServiceCollection();
            services.AddSqlServerDatabase(SqlServerTestServer.AdminConnectionString, _ => { });
            services.AddSqlServerRepositories();
            _provider = services.BuildServiceProvider();
        }

        return new LockHolder(_provider.CreateScope(),
            sp => sp.GetRequiredService<SqlServerDefaultUnitOfWork>().BeginTransactionAsync(),
            sp => sp.GetRequiredService<SqlServerDefaultUnitOfWork>().CommitTransactionAsync(),
            sp => sp.GetRequiredService<SqlServerDefaultUnitOfWork>().RollbackTransactionAsync());
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
            var session = (await ((RelationalUnitOfWork)victim.Locks).QueryAsync<SessionRow>(
                "SELECT CAST(@@SPID AS bigint) AS value"))[0].Value;
            // KILL takes no parameter: the session id is a number the server just answered.
            await ((RelationalUnitOfWork)killer.Locks).ExecuteAsync($"KILL {session}");
        });

    private sealed class SessionRow
    {
        public long Value { get; set; }
    }

    [Test]
    public async Task The_server_holds_an_application_lock_on_the_documented_resource()
    {
        using var holder = Holder();
        var name = NamedLockScenarios.Unique("tenant");
        var resource = SqlServerNamedLocks.Resource(name);

        await holder.BeginTransaction();
        await holder.Acquire(name, TimeSpan.FromSeconds(5));
        Assert.That(await Grantable(resource), Is.False, "the transaction holds an exclusive application lock on the resource");

        await holder.CommitTransaction();
        Assert.That(await Grantable(resource), Is.True, "the commit released it");

        await using (await holder.Acquire(name, TimeSpan.FromSeconds(5)))
        {
            Assert.That(await Grantable(resource), Is.False, "a pinned session holds it outside a transaction");
        }

        Assert.That(await Grantable(resource), Is.True, "disposing the handle released it");
    }

    [Test]
    public async Task A_lock_taken_by_hand_on_the_same_resource_excludes_the_unit_of_work()
    {
        using var holder = Holder();
        var name = NamedLockScenarios.Unique("tenant");

        await using var connection = new SqlConnection(SqlServerTestServer.AdminConnectionString);
        await connection.OpenAsync();
        await Execute(connection, "EXEC sp_getapplock @Resource = @r, @LockMode = 'Exclusive', @LockOwner = 'Session'",
            SqlServerNamedLocks.Resource(name));

        Assert.That(async () => await holder.Acquire(name, TimeSpan.FromMilliseconds(300)), Throws.InstanceOf<TimeoutException>());

        await Execute(connection, "EXEC sp_releaseapplock @Resource = @r, @LockOwner = 'Session'", SqlServerNamedLocks.Resource(name));
        await using var taken = await holder.Acquire(name, TimeSpan.FromSeconds(5));
    }

    /// <summary>Whether another session could take an exclusive application lock on the resource right now.</summary>
    private static async Task<bool> Grantable(string resource)
    {
        await using var connection = new SqlConnection(SqlServerTestServer.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT APPLOCK_TEST('public', @r, 'Exclusive', 'Session')", connection);
        command.Parameters.AddWithValue("r", resource);
        return (int)(await command.ExecuteScalarAsync())! == 1;
    }

    private static async Task Execute(SqlConnection connection, string sql, string resource)
    {
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("r", resource);
        await command.ExecuteNonQueryAsync();
    }
}
