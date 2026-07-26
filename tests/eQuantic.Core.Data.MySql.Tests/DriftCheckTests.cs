using System.Data.Common;
using eQuantic.Core.Data.Evolution;

namespace eQuantic.Core.Data.MySql.Tests;

/// <summary>
///     The drift check against MySQL, which reads its catalogue in an entirely different way from PostgreSQL —
///     one string per column rather than a composed type — so a clean database staying silent here is a separate
///     fact, not a corollary of the other. MySQL is where the risk lives: the dialect writes <c>tinyint(1)</c> for
///     a boolean and <c>datetime(6)</c> for a timestamp, and a catalogue read that dropped those would report
///     every one of them.
/// </summary>
[TestFixture]
public sealed class DriftCheckTests : MySqlIntegrationTest
{
    private static async Task<DriftReport> CheckAsync(MySqlTestDatabase db)
    {
        var source = db.Resolve<IDatabaseSnapshotSource>();
        return DriftComparer.Compare(source.Expect(), await source.ObserveAsync());
    }

    [Test]
    public async Task A_schema_the_engine_created_reports_nothing()
    {
        using var db = await NewSchemaAsync();

        var report = await CheckAsync(db);

        Assert.That(report.Findings, Is.Empty,
            "the type MySQL reports must be the one the dialect writes, display widths and all");
    }

    [Test]
    public async Task A_column_dropped_by_hand_is_found()
    {
        using var db = await NewSchemaAsync();

        await using (var connection = await db.Resolve<DbDataSource>().OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE sale_orders DROP COLUMN customer";
            await command.ExecuteNonQueryAsync();
        }

        var finding = (await CheckAsync(db)).Findings.Single();

        Assert.That(finding.Kind, Is.EqualTo(DriftKind.MissingField));
        Assert.That(finding.Field, Is.EqualTo("customer"));
    }
}
