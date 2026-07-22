using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>
///     Proves the concurrency-token semantics against a real cluster: writes on a token entity are lightweight
///     transactions, a lost race throws instead of silently overwriting, and the table's declared TTL lands in
///     the schema.
/// </summary>
[TestFixture]
public sealed class CassandraConcurrencyTests : CassandraIntegrationTest
{
    private static IAsyncRepository<Ledger, Guid> Ledgers(CassandraTestDatabase db) =>
        db.Resolve<IAsyncRepository<Ledger, Guid>>();

    [Test]
    public async Task First_commit_writes_version_one_and_a_stale_writer_loses()
    {
        using var db = await NewSchemaAsync();
        var repo = Ledgers(db);

        var ledger = new Ledger { Holder = "ana", Balance = 100m };
        await repo.AddAsync(ledger);
        await Uow(db).CommitAsync();
        Assert.That(ledger.Version, Is.EqualTo(1), "the first persisted version is 1 (INSERT ... IF NOT EXISTS)");

        // Two independent readers load the same row, and both try to write.
        var first = await repo.GetAsync(ledger.Id);
        var second = await repo.GetAsync(ledger.Id);
        Assert.That(first!.Version, Is.EqualTo(1));

        first.Balance = 150m;
        await repo.ModifyAsync(first);
        await Uow(db).CommitAsync();
        Assert.That(first.Version, Is.EqualTo(2), "the winning write bumped the version");

        second!.Balance = 999m;
        await repo.ModifyAsync(second);
        Assert.ThrowsAsync<ConcurrencyConflictException>(() => Uow(db).CommitAsync(),
            "the stale write's UPDATE ... IF version = 1 must not apply");

        var current = await repo.GetAsync(ledger.Id);
        Assert.That(current!.Balance, Is.EqualTo(150m), "the winning write survived; the stale one changed nothing");
    }

    [Test]
    public async Task Adding_the_same_key_twice_is_a_conflict_not_an_overwrite()
    {
        using var db = await NewSchemaAsync();
        var repo = Ledgers(db);

        var id = Guid.NewGuid();
        await repo.AddAsync(new Ledger { Id = id, Holder = "ana", Balance = 1m });
        await Uow(db).CommitAsync();

        await repo.AddAsync(new Ledger { Id = id, Holder = "bia", Balance = 2m });
        Assert.ThrowsAsync<ConcurrencyConflictException>(() => Uow(db).CommitAsync(),
            "INSERT ... IF NOT EXISTS refuses the duplicate instead of upserting over it");
    }

    [Test]
    public async Task A_token_write_refuses_the_logged_batch()
    {
        using var db = await NewSchemaAsync();
        var repo = Ledgers(db);
        var uow = Uow(db);

        await uow.BeginTransactionAsync();
        Assert.ThrowsAsync<NotSupportedException>(async () => await repo.AddAsync(new Ledger { Holder = "ana" }),
            "a conditional (LWT) write cannot join a LOGGED BATCH; the combination refuses instead of degrading");
        await uow.RollbackTransactionAsync();
    }

    [Test]
    public async Task The_declared_ttl_lands_in_the_table_schema()
    {
        using var db = await NewSchemaAsync();

        var row = (await db.Session.ExecuteAsync(new global::Cassandra.SimpleStatement(
                "SELECT default_time_to_live FROM system_schema.tables WHERE keyspace_name = ? AND table_name = 'ledgers'",
                db.Keyspace)))
            .First();
        Assert.That(row.GetValue<int>("default_time_to_live"), Is.EqualTo((int)TimeSpan.FromDays(30).TotalSeconds),
            "EnsureCollection() applied the model's TimeToLive as the table's default_time_to_live");
    }
}
