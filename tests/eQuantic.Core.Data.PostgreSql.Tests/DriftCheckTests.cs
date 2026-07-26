using System.Data.Common;
using eQuantic.Core.Data.Evolution;

namespace eQuantic.Core.Data.PostgreSql.Tests;

/// <summary>
///     What a drift check has to get right against a real database. The first test is the one that decides whether
///     the feature is worth having: a schema the engine itself created must produce <b>no findings at all</b>. A
///     check that reports a handful of spelling differences on a healthy database teaches people to ignore it, and
///     then it is worse than nothing — it is a green light nobody reads.
/// </summary>
[TestFixture]
public sealed class DriftCheckTests : PostgreSqlIntegrationTest
{
    private static async Task<DriftReport> CheckAsync(PostgreSqlTestDatabase db)
    {
        var source = db.Resolve<IDatabaseSnapshotSource>();
        return DriftComparer.Compare(source.Expect(), await source.ObserveAsync());
    }

    private static async Task AlterAsync(PostgreSqlTestDatabase db, string sql)
    {
        await using var connection = await db.Resolve<DbDataSource>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Test]
    public async Task A_schema_the_engine_created_reports_nothing()
    {
        using var db = await NewSchemaAsync();

        var report = await CheckAsync(db);

        Assert.That(report.Findings, Is.Empty,
            "every type the model declares must match the one PostgreSQL reports, or the check cries wolf");
        Assert.That(report.IsClean, Is.True);
    }

    [Test]
    public async Task A_column_dropped_by_hand_is_found()
    {
        using var db = await NewSchemaAsync();
        await AlterAsync(db, "ALTER TABLE sale_orders DROP COLUMN customer");

        var report = await CheckAsync(db);

        var finding = report.Findings.Single();
        Assert.That(finding.Kind, Is.EqualTo(DriftKind.MissingField));
        Assert.That(finding.Field, Is.EqualTo("customer"));
        Assert.That(report.Breaks, Is.True, "the application reads that column on every query");
    }

    [Test]
    public async Task A_table_dropped_by_hand_is_found()
    {
        using var db = await NewSchemaAsync();
        await AlterAsync(db, "DROP TABLE sale_orders CASCADE");

        var report = await CheckAsync(db);

        Assert.That(report.Findings.Single(f => f.Collection == "sale_orders").Kind,
            Is.EqualTo(DriftKind.MissingCollection));
    }

    [Test]
    public async Task A_column_retyped_by_hand_is_found()
    {
        using var db = await NewSchemaAsync();
        await AlterAsync(db, "ALTER TABLE sale_orders ALTER COLUMN customer TYPE varchar(20)");

        var finding = (await CheckAsync(db)).Findings.Single();

        Assert.That(finding.Kind, Is.EqualTo(DriftKind.TypeDiffers));
        Assert.That(finding.Found, Is.EqualTo("varchar(20)"), "reported as the dialect spells it, not as the catalogue does");
    }

    [Test]
    public async Task A_column_tightened_by_hand_is_found()
    {
        using var db = await NewSchemaAsync();
        await AlterAsync(db, "ALTER TABLE sale_orders ALTER COLUMN total SET NOT NULL");

        var finding = (await CheckAsync(db)).Findings.Single();

        // The engine writes no NOT NULL of its own, so a column that has one was tightened by somebody. It is
        // worth reporting either way round: this is the constraint that starts rejecting writes the code allows.
        Assert.That(finding.Kind, Is.EqualTo(DriftKind.NullabilityDiffers));
        Assert.That(finding.Field, Is.EqualTo("total"));
        Assert.That(finding.Expected, Is.EqualTo("null allowed"));
        Assert.That(finding.Found, Is.EqualTo("not null"));
    }

    [Test]
    public async Task A_column_nobody_mapped_is_reported_without_being_treated_as_a_fault()
    {
        using var db = await NewSchemaAsync();
        await AlterAsync(db, "ALTER TABLE sale_orders ADD COLUMN legacy_note text");

        var report = await CheckAsync(db);

        var finding = report.Findings.Single();
        Assert.That(finding.Kind, Is.EqualTo(DriftKind.UnexpectedField));
        Assert.That(report.Breaks, Is.False, "a database gets shared; another application's column is not a fault here");
    }

    [Test]
    public async Task A_table_nobody_mapped_is_not_reported_at_all()
    {
        using var db = await NewSchemaAsync();
        await AlterAsync(db, "CREATE TABLE somebody_elses (id integer)");

        Assert.That((await CheckAsync(db)).Findings, Is.Empty,
            "reading every table in a shared database would bury the findings that matter");
    }
}
