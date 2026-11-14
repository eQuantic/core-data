using System.Data.Common;
using eQuantic.Core.Data.Evolution;

namespace eQuantic.Core.Data.SqlServer.Tests;

/// <summary>
///     The drift check against SQL Server, whose catalogue keeps a text column's length in bytes and a decimal's
///     facets apart from its name, so a clean database staying silent here is its own fact. The schema carries what
///     SQL Server stores differently from the other dialects: an enum's labels as text held to them by a
///     <c>CHECK</c>, JSON as text checked by <c>ISJSON</c>, a sequence object behind a store-generated column and the
///     clock as a default, all of them <c>NOT NULL</c>.
/// </summary>
[TestFixture]
public sealed class DriftCheckTests : SqlServerIntegrationTest
{
    private static async Task<DriftReport> CheckAsync(SqlServerTestDatabase db)
    {
        var source = db.Resolve<IDatabaseSnapshotSource>();
        return DriftComparer.Compare(source.Expect(), await source.ObserveAsync());
    }

    private static async Task AlterAsync(SqlServerTestDatabase db, string sql)
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
            "every type the model declares must match the one SQL Server reports, or the check cries wolf");
    }

    [Test]
    public async Task An_enum_column_still_holding_numbers_is_found()
    {
        using var db = await NewSchemaAsync();

        // A model that stores RunState as its labels, against a column that still holds its numbers: the CHECK on
        // the labels goes first, since a column it names cannot change its type.
        await AlterAsync(db,
            """
            DECLARE @check sysname = (SELECT name FROM sys.check_constraints
                                      WHERE parent_object_id = OBJECT_ID(N'run_record')
                                        AND definition LIKE N'%\[state\]%' ESCAPE N'\');
            DECLARE @drop nvarchar(400) = N'ALTER TABLE run_record DROP CONSTRAINT ' + QUOTENAME(@check);
            EXEC (@drop);
            ALTER TABLE run_record ALTER COLUMN state int NULL;
            """);

        var finding = (await CheckAsync(db)).Findings.Single();

        Assert.That(finding.Kind, Is.EqualTo(DriftKind.TypeDiffers));
        Assert.That(finding.Field, Is.EqualTo("state"));
        Assert.That(finding.Expected, Is.EqualTo("nvarchar(11)"), "text as wide as the longest label");
        Assert.That(finding.Found, Is.EqualTo("int"));
        Assert.That(finding.Breaks, Is.True);
    }
}
