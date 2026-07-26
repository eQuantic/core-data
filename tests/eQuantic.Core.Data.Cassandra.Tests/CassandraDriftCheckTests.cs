using Cassandra;
using eQuantic.Core.Data.Evolution;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>
///     The drift check against a real Cassandra keyspace. Cassandra is the only non-relational store this can be
///     asked of honestly, because it is the only one that keeps a schema to read — and the one where the most
///     valuable answer is about the partition key, which no migration can change once rows exist.
///     <para>
///         As everywhere, the test that decides whether the feature is worth having is the first: a keyspace the
///         engine created must produce nothing at all.
///     </para>
/// </summary>
[TestFixture]
public sealed class CassandraDriftCheckTests : CassandraIntegrationTest
{
    private static async Task<DriftReport> CheckAsync(CassandraTestDatabase db)
    {
        var source = db.Resolve<IDatabaseSnapshotSource>();
        return DriftComparer.Compare(source.Expect(), await source.ObserveAsync());
    }

    private static Task AlterAsync(CassandraTestDatabase db, string cql) =>
        db.Resolve<ISession>().ExecuteAsync(new SimpleStatement(cql));

    [Test]
    public async Task A_keyspace_the_engine_created_reports_nothing()
    {
        using var db = await NewSchemaAsync();

        var report = await CheckAsync(db);

        Assert.That(report.Findings, Is.Empty,
            "every CQL type the model declares must match the one system_schema reports");
        Assert.That(report.IsClean, Is.True);
    }

    [Test]
    public async Task A_column_dropped_by_hand_is_found()
    {
        using var db = await NewSchemaAsync();
        await AlterAsync(db, "ALTER TABLE accounts DROP balance");

        var finding = (await CheckAsync(db)).Findings.Single();

        Assert.That(finding.Kind, Is.EqualTo(DriftKind.MissingField));
        Assert.That(finding.Field, Is.EqualTo("balance"));
        Assert.That(finding.Breaks, Is.True);
    }

    [Test]
    public async Task A_column_nobody_mapped_is_reported_without_being_treated_as_a_fault()
    {
        using var db = await NewSchemaAsync();
        await AlterAsync(db, "ALTER TABLE accounts ADD legacy_note text");

        var report = await CheckAsync(db);

        Assert.That(report.Findings.Single().Kind, Is.EqualTo(DriftKind.UnexpectedField));
        Assert.That(report.Breaks, Is.False, "a keyspace is shared; another application's column is not a fault");
    }

    [Test]
    public async Task A_table_nobody_mapped_is_not_read_at_all()
    {
        using var db = await NewSchemaAsync();
        await AlterAsync(db, "CREATE TABLE somebody_elses (id int PRIMARY KEY)");

        Assert.That((await CheckAsync(db)).Findings, Is.Empty);
    }

    [Test]
    public async Task A_table_dropped_by_hand_is_found()
    {
        using var db = await NewSchemaAsync();
        await AlterAsync(db, "DROP TABLE accounts");

        Assert.That((await CheckAsync(db)).Findings.Single(f => f.Collection == "accounts").Kind,
            Is.EqualTo(DriftKind.MissingCollection));
    }

    [Test]
    public async Task A_table_built_with_a_different_partition_key_is_found_and_named_as_unmigratable()
    {
        using var db = await NewSchemaAsync();

        // The one difference no ALTER closes: rebuilt by hand under the wrong key, as a restore or a hand-run
        // script would leave it.
        await AlterAsync(db, "DROP TABLE accounts");
        await AlterAsync(db,
            "CREATE TABLE accounts (id uuid, owner text, balance decimal, active boolean, tags list<text>, " +
            "opened_at timestamp, PRIMARY KEY (owner, id))");

        var report = await CheckAsync(db);
        var finding = report.Findings.Single(f => f.Kind == DriftKind.PartitionKeyDiffers);

        Assert.That(finding.Expected, Is.EqualTo("id"));
        Assert.That(finding.Found, Is.EqualTo("owner"));
        Assert.That(report.NeedsRebuild, Is.True,
            "there is no migration for this — only a new table and a copy, which the report has to say");
    }
}
