using System.Diagnostics;
using eQuantic.Core.Data.Diagnostics;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Read;
using global::Cassandra;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>
///     Exercises the enterprise surface against a real cluster: global query filters (fixed and scoped to
///     set-based writes), the <c>IgnoringQueryFilters</c> opt-out, per-query consistency, per-commit TTL, and the
///     activities emitted on <see cref="DataActivitySource" />.
/// </summary>
[TestFixture]
public sealed class CassandraEnterpriseTests : CassandraIntegrationTest
{
    private static DateTime Utc(int hour) => new(2026, 1, 1, hour, 0, 0, DateTimeKind.Utc);

    private static Reading NewReading(int sensor, DateTime at) =>
        new() { SensorId = sensor, At = at, Value = 1.0, Quality = "ok" };

    private static Action<IServiceCollection> SensorOneOnly => services =>
        services.AddSingleton(new QueryFilters().For<Reading>(x => x.SensorId == 1));

    // ---------------------------------------------------------------- global query filters

    [Test]
    public async Task Global_filter_scopes_every_read_and_ignoring_opts_out()
    {
        using var db = await NewSchemaAsync(SensorOneOnly);
        var repo = db.Resolve<IAsyncRepository<Reading, int>>();
        await Seed(db, NewReading(1, Utc(0)), NewReading(1, Utc(1)), NewReading(2, Utc(0)));

        Assert.That((await repo.GetAllAsync()).Select(x => x.SensorId), Is.All.EqualTo(1), "the global filter scopes the read");
        Assert.That(await repo.CountAsync(), Is.EqualTo(2));
        Assert.That(await repo.CountAsync(new QueryOptions<Reading>().IgnoringQueryFilters()), Is.EqualTo(3), "IgnoringQueryFilters opts out");
    }

    [Test]
    public async Task Global_filter_scopes_set_based_deletes()
    {
        using var db = await NewSchemaAsync(SensorOneOnly);
        var repo = db.Resolve<IAsyncRepository<Reading, int>>();
        await Seed(db, NewReading(1, Utc(0)), NewReading(2, Utc(0)));

        var removed = await repo.DeleteManyAsync(x => x.At == Utc(0));

        Assert.That(removed, Is.EqualTo(1), "only the tenant's row is deleted");
        var all = await repo.GetAllAsync(new QueryOptions<Reading>().IgnoringQueryFilters());
        Assert.That(all.Select(x => x.SensorId), Is.EquivalentTo(new[] { 2 }), "the other tenant's row survives");
    }

    [Test]
    public async Task Explain_reports_the_global_filter()
    {
        using var db = await NewSchemaAsync(SensorOneOnly);
        var explainable = (IExplainableRepository<Reading>)db.Resolve<IAsyncRepository<Reading, int>>();

        var plan = explainable.Explain();

        Assert.That(plan.Statement, Does.Contain("SensorId = ?"), "the global filter is pushed down");
        Assert.That(plan.Notes, Has.Some.Contains("global query filter"));
    }

    // ---------------------------------------------------------------- consistency + TTL

    [Test]
    public async Task A_query_can_run_at_an_explicit_consistency_level()
    {
        using var db = await NewSchemaAsync();
        var repo = db.Resolve<IAsyncRepository<Reading, int>>();
        await Seed(db, NewReading(1, Utc(0)));

        var options = new QueryOptions<Reading>().Where(x => x.SensorId == 1).WithConsistency(ConsistencyLevel.LocalQuorum);

        Assert.That((await repo.GetAllAsync(options)).Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task A_commit_with_ttl_writes_expiring_rows()
    {
        using var db = await NewSchemaAsync();
        var repo = AccountRepo(db);
        var account = new Account { Id = Guid.NewGuid(), Owner = "ephemeral", Tags = [], OpenedAt = Utc(0) };

        await repo.AddAsync(account);
        await Uow(db).CommitAsync(o => o.WithTtl(TimeSpan.FromHours(1)));

        var row = db.Session.Execute(new SimpleStatement(
            $"SELECT TTL(Owner) FROM {db.Keyspace}.accounts WHERE Id = ?", account.Id)).First();
        Assert.That(row.GetValue<int?>(0), Is.GreaterThan(0), "the row carries a TTL");
    }

    // ---------------------------------------------------------------- telemetry

    [Test]
    public async Task Reads_and_commits_emit_activities_on_the_shared_source()
    {
        using var db = await NewSchemaAsync();
        var repo = db.Resolve<IAsyncRepository<Reading, int>>();

        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DataActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);

        await repo.AddAsync(NewReading(1, Utc(0)));
        await Uow(db).CommitAsync();
        _ = await repo.GetAllAsync(new QueryOptions<Reading>().Where(x => x.SensorId == 1));

        Assert.That(stopped.Select(activity => activity.OperationName), Does.Contain("cassandra.commit"));
        Assert.That(stopped.Select(activity => activity.OperationName), Does.Contain("cassandra.select"));
        var execute = stopped.First(activity => activity.OperationName == "cassandra.execute" && activity.GetTagItem("db.statement") is string statement && statement.StartsWith("SELECT"));
        Assert.That(execute.GetTagItem("db.statement"), Does.Contain("SensorId = ?"), "the span carries the statement with placeholders");
    }
}
