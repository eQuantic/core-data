using eQuantic.Core.Data.Migration;
using global::Cassandra;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>
///     Covers the Cassandra migration runner, executor and history against a real cluster: it creates the declared
///     tables (with their partition/clustering keys and column types) and a single-column secondary index, records
///     what it applied in the <c>schema_migrations</c> table, and is safe to re-run.
/// </summary>
[TestFixture]
public sealed class CassandraMigrationTests : CassandraIntegrationTest
{
    [Test]
    public async Task Runner_creates_the_declared_tables_and_records_history()
    {
        using var db = NewDatabase(typeof(SchemaSetupMigration).Assembly);

        var applied = await db.Resolve<IMigrationRunner>().RunAsync();
        Assert.That(applied, Is.EqualTo(1));

        var tables = await TableNames(db);
        Assert.That(tables, Does.Contain("accounts"));
        Assert.That(tables, Does.Contain("readings"));

        var recorded = (await db.Session.ExecuteAsync(new SimpleStatement("SELECT id, title FROM schema_migrations"))).ToList();
        Assert.That(recorded, Has.Count.EqualTo(1));
        Assert.That(recorded[0].GetValue<string>("title"), Is.EqualTo("Cassandra schema setup"));
    }

    [Test]
    public async Task Runner_applies_pending_once_then_skips_on_the_second_run()
    {
        using var db = NewDatabase(typeof(SchemaSetupMigration).Assembly);
        var runner = db.Resolve<IMigrationRunner>();

        Assert.That(await runner.RunAsync(), Is.EqualTo(1));
        Assert.That(await runner.RunAsync(), Is.EqualTo(0), "already recorded — nothing to re-apply");
    }

    [Test]
    public async Task Ensure_collection_creates_the_declared_primary_key()
    {
        using var db = NewDatabase(typeof(SchemaSetupMigration).Assembly);
        await db.Resolve<IMigrationRunner>().RunAsync();

        var kinds = (await db.Session.ExecuteAsync(new SimpleStatement(
                "SELECT column_name, kind FROM system_schema.columns WHERE keyspace_name = ? AND table_name = 'readings'",
                db.Keyspace)))
            .ToDictionary(row => row.GetValue<string>("column_name"), row => row.GetValue<string>("kind"));

        Assert.That(kinds["sensorid"], Is.EqualTo("partition_key"));
        Assert.That(kinds["at"], Is.EqualTo("clustering"));
        Assert.That(kinds["value"], Is.EqualTo("regular"));
    }

    [Test]
    public async Task Ensure_index_creates_a_secondary_index()
    {
        using var db = NewDatabase(typeof(SchemaSetupMigration).Assembly);
        await db.Resolve<IMigrationRunner>().RunAsync();

        var indexes = (await db.Session.ExecuteAsync(new SimpleStatement(
                "SELECT index_name FROM system_schema.indexes WHERE keyspace_name = ? AND table_name = 'accounts'",
                db.Keyspace)))
            .Select(row => row.GetValue<string>("index_name"))
            .ToList();

        Assert.That(indexes, Is.Not.Empty, "the migration declared an index on accounts(Owner)");
    }

    private static async Task<List<string>> TableNames(CassandraTestDatabase db) =>
        (await db.Session.ExecuteAsync(new SimpleStatement(
            "SELECT table_name FROM system_schema.tables WHERE keyspace_name = ?", db.Keyspace)))
        .Select(row => row.GetValue<string>("table_name"))
        .ToList();
}
