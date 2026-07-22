using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.MySql.Extensions;
using eQuantic.Core.Data.MySql.Repository;
using eQuantic.Core.Data.Repository;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MariaDb;

namespace eQuantic.Core.Data.MySql.Tests;

/// <summary>
///     Exercises the MariaDB dialect against a real server: the same MySQL engine and driver, plus
///     <c>INSERT … RETURNING</c> — generated keys are read back into the entities, the capability the MySQL
///     dialect honestly rejects. The fixture boots its own container and shares one schema across its tests.
/// </summary>
[TestFixture]
public sealed class MariaDbTests
{
    private MariaDbContainer? _container;
    private ServiceProvider? _provider;
    private IServiceScope? _scope;
    private Exception? _startupError;

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        try
        {
            _container = new MariaDbBuilder().Build();
            await _container.StartAsync();

            var services = new ServiceCollection();
            services.AddMariaDbDatabase(_container.GetConnectionString(), TestSchema.Configure);
            services.AddMySqlRepositories();
            services.AddMySqlMigrations(typeof(MariaDbTests).Assembly);
            _provider = services.BuildServiceProvider();
            _scope = _provider.CreateScope();
            await _scope.ServiceProvider.GetRequiredService<IMigrationRunner>().RunAsync();
        }
        catch (Exception ex)
        {
            _startupError = ex;
        }
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        _scope?.Dispose();
        _provider?.Dispose();
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

    [Test]
    public async Task Generated_keys_are_read_back_with_returning()
    {
        var repo = _scope!.ServiceProvider.GetRequiredService<IAsyncRepository<Ticket, long>>();
        var uow = _scope.ServiceProvider.GetRequiredService<MySqlDefaultUnitOfWork>();

        var first = new Ticket { Label = "one" };
        var second = new Ticket { Label = "two" };
        await repo.AddAsync(first);
        await repo.AddAsync(second);
        await uow.CommitAsync();

        Assert.That(first.Id, Is.GreaterThan(0), "RETURNING backfilled the identity — the capability MySQL rejects");
        Assert.That(second.Id, Is.GreaterThan(first.Id));
        Assert.That((await repo.GetAsync(second.Id))!.Label, Is.EqualTo("two"));
    }

    [Test]
    public async Task Round_trip_and_pushdown_run_on_the_shared_engine()
    {
        var repo = _scope!.ServiceProvider.GetRequiredService<IAsyncRepository<SaleOrder, Guid>>();
        var uow = _scope.ServiceProvider.GetRequiredService<MySqlDefaultUnitOfWork>();

        var marker = Guid.NewGuid().ToString("N")[..12];
        await repo.AddAsync(new SaleOrder
        {
            Id = Guid.NewGuid(),
            Customer = marker,
            Total = 12.5m,
            Status = "open",
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await uow.CommitAsync();

        var found = await repo.GetFilteredAsync(x => x.Customer == marker || x.Total > 9999m);
        Assert.That(found.Single().Total, Is.EqualTo(12.5m), "native OR pushdown over the MariaDB dialect");
    }
}
