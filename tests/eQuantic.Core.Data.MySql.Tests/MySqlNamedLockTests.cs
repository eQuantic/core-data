using eQuantic.Core.Data.MySql.Extensions;
using eQuantic.Core.Data.MySql.Repository;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Testcontainers.MariaDb;

namespace eQuantic.Core.Data.MySql.Tests;

/// <summary>
///     Named locks against a real server, MySQL or MariaDB: <c>GET_LOCK</c> on a pinned session outside a transaction,
///     and on the transaction's own session inside one, released by the engine once the transaction commits or rolls
///     back (no transaction releases a <c>GET_LOCK</c>). Each holder is a unit of work in its own scope.
/// </summary>
public abstract class NamedLockSuite
{
    private ServiceProvider? _provider;

    /// <summary>The server's connection string.</summary>
    protected abstract string ConnectionString { get; }

    /// <summary>Registers the database (MySQL or MariaDB) for the connection string.</summary>
    protected abstract void AddDatabase(IServiceCollection services, string connectionString);

    [OneTimeTearDown]
    public void DisposeProvider() => _provider?.Dispose();

    private LockHolder Holder()
    {
        if (_provider is null)
        {
            var services = new ServiceCollection();
            AddDatabase(services, ConnectionString);
            services.AddMySqlRepositories();
            _provider = services.BuildServiceProvider();
        }

        return new LockHolder(_provider.CreateScope(),
            sp => sp.GetRequiredService<MySqlDefaultUnitOfWork>().BeginTransactionAsync(),
            sp => sp.GetRequiredService<MySqlDefaultUnitOfWork>().CommitTransactionAsync(),
            sp => sp.GetRequiredService<MySqlDefaultUnitOfWork>().RollbackTransactionAsync());
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
    public async Task The_server_holds_the_documented_lock_until_the_transaction_ends()
    {
        using var holder = Holder();
        var name = NamedLockScenarios.Unique("tenant");
        var lockName = MySqlNamedLocks.LockName(name);

        await holder.BeginTransaction();
        await holder.Acquire(name, TimeSpan.FromSeconds(5));
        Assert.That(await IsFree(lockName), Is.False, "GET_LOCK holds the documented name for the transaction's session");

        await holder.CommitTransaction();
        Assert.That(await IsFree(lockName), Is.True, "the engine released it once the transaction committed");

        await using (await holder.Acquire(name, TimeSpan.FromSeconds(5)))
        {
            Assert.That(await IsFree(lockName), Is.False, "a pinned session holds it outside a transaction");
        }

        Assert.That(await IsFree(lockName), Is.True, "disposing the handle released it");
    }

    [Test]
    public async Task A_lock_taken_by_hand_on_the_same_name_excludes_the_unit_of_work()
    {
        using var holder = Holder();
        var name = NamedLockScenarios.Unique("tenant");

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        Assert.That(await Scalar(connection, "SELECT GET_LOCK(@n, 0)", MySqlNamedLocks.LockName(name)), Is.EqualTo(1));

        Assert.That(async () => await holder.Acquire(name, TimeSpan.FromMilliseconds(300)), Throws.InstanceOf<TimeoutException>());

        await Scalar(connection, "SELECT RELEASE_LOCK(@n)", MySqlNamedLocks.LockName(name));
        await using var taken = await holder.Acquire(name, TimeSpan.FromSeconds(5));
    }

    private async Task<bool> IsFree(string lockName)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return await Scalar(connection, "SELECT IS_FREE_LOCK(@n)", lockName) == 1;
    }

    private static async Task<long> Scalar(MySqlConnection connection, string sql, string lockName)
    {
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("n", lockName);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}

/// <summary>The named-lock suite against MySQL (the suite's shared container).</summary>
[TestFixture]
public sealed class MySqlNamedLockTests : NamedLockSuite
{
    [SetUp]
    public void RequireDocker() => MySqlTestServer.EnsureAvailable();

    protected override string ConnectionString => MySqlTestServer.AdminConnectionString;

    protected override void AddDatabase(IServiceCollection services, string connectionString) =>
        services.AddMySqlDatabase(connectionString, _ => { });
}

/// <summary>The named-lock suite against MariaDB (a container of its own, for this fixture).</summary>
[TestFixture]
public sealed class MariaDbNamedLockTests : NamedLockSuite
{
    private MariaDbContainer? _container;
    private Exception? _startupError;

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        try
        {
            _container = new MariaDbBuilder().Build();
            await _container.StartAsync();
        }
        catch (Exception exception)
        {
            _startupError = exception;
        }
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [SetUp]
    public void RequireDocker()
    {
        if (_startupError is not null)
        {
            Assert.Ignore($"MariaDB test container is unavailable (Docker required): {_startupError.Message}");
        }
    }

    protected override string ConnectionString => _container!.GetConnectionString();

    protected override void AddDatabase(IServiceCollection services, string connectionString) =>
        services.AddMariaDbDatabase(connectionString, _ => { });
}
