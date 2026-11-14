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

    [Test]
    public async Task An_enum_column_still_holding_numbers_is_found()
    {
        using var db = await NewSchemaAsync();

        // A model that stores RunState as its labels, against a column that still holds its numbers.
        await using (var connection = await db.Resolve<DbDataSource>().OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE run_record MODIFY state int NULL";
            await command.ExecuteNonQueryAsync();
        }

        var finding = (await CheckAsync(db)).Findings.Single();

        Assert.That(finding.Kind, Is.EqualTo(DriftKind.TypeDiffers));
        Assert.That(finding.Field, Is.EqualTo("state"));
        Assert.That(finding.Expected, Is.EqualTo("enum('queued','in_progress','completed')"));
        Assert.That(finding.Found, Is.EqualTo("int"));
        Assert.That(finding.Breaks, Is.True);
    }
}
