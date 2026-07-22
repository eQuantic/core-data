using eQuantic.Core.Data.Migration;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>The Cassandra mapping shared by the integration tests: the two test tables and their keys.</summary>
internal static class TestSchema
{
    /// <summary>
    ///     Maps <see cref="Account" /> (partition key only), <see cref="Reading" /> (partition + clustering) and
    ///     <see cref="Tally" /> (a counter table).
    /// </summary>
    public static void Configure(CassandraModelBuilder builder) => builder
        .Entity<Account>(entity => entity
            .Table("accounts")
            .PartitionKey(x => x.Id))
        .Entity<Reading>(entity => entity
            .Table("readings")
            .PartitionKey(x => x.SensorId)
            .ClusteringKey(x => x.At)
            .SearchIndex(x => x.Quality))
        .Entity<Tally>(entity => entity
            .Table("tallies")
            .PartitionKey(x => x.Space)
            .Counter(x => x.Hits))
        .Entity<Ledger>(entity => entity
            .Table("ledgers")
            .PartitionKey(x => x.Id)
            .ConcurrencyToken(x => x.Version)
            .TimeToLive(TimeSpan.FromDays(30)));
}

/// <summary>A versioned row: writes are lightweight transactions, and the table carries a default TTL.</summary>
public sealed class Ledger : eQuantic.Core.Data.Repository.IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Holder { get; set; } = "";

    public decimal Balance { get; set; }

    public long Version { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>
///     The schema migration the runner discovers: it creates both tables from the model (partition/clustering keys
///     and column types) and a single-column secondary index on <c>accounts(Owner)</c> — proving the executor emits
///     the <c>CREATE TABLE</c> and <c>CREATE INDEX</c> DDL end-to-end against a real cluster.
/// </summary>
[Migration("Cassandra schema setup", 2026, 1, 1, 0, 0, 0)]
public sealed class SchemaSetupMigration : Data.Migration.Migration
{
    /// <inheritdoc />
    public override void Up(IMigrationBuilder migration) => migration
        .For<Account>(account => account
            .EnsureCollection()
            .Index(x => x.Owner))
        .For<Reading>(reading => reading
            .EnsureCollection())
        .For<Tally>(tally => tally
            .EnsureCollection())
        .For<Ledger>(ledger => ledger
            .EnsureCollection());
}
