using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Write;
using global::Cassandra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>
///     One serial consistency for every lightweight transaction and every serial read, against a real Cassandra: the
///     session's (<c>QueryOptions.SetSerialConsistencyLevel</c>), <c>SERIAL</c> unless the cluster was built otherwise. A
///     compare-and-set pinned to one and a read at another would not agree on what the transaction decided, so the provider
///     pins every transaction to it and reads at it — the sequences, the leases of named locks, inserts, upserts, conditional
///     and computed set-based writes, and the token writes of a commit.
/// </summary>
[TestFixture]
public sealed class CassandraSerialConsistencyTests : CassandraIntegrationTest
{
    private static void Model(CassandraModelBuilder builder) => builder
        .Entity<Voucher>(entity => entity
            .Table("vouchers")
            .PartitionKey(x => x.Id)
            .Generated(x => x.Number)
            .Generated(x => x.IssuedAt, Modeling.StoreValue.Clock))
        .Entity<QueuedJob>(entity => entity.Table("queued_jobs").PartitionKey(x => x.Id))
        .Entity<ItemType>(entity => entity.Table("item_types").PartitionKey(x => x.Id))
        .Entity<Ledger>(entity => entity.Table("ledgers").PartitionKey(x => x.Id).ConcurrencyToken(x => x.Version));

    private static async Task<CassandraTestDatabase> OpenAsync(ConsistencyLevel? serial)
    {
        var keyspace = "ks_" + Guid.NewGuid().ToString("N");
        var db = new CassandraTestDatabase(CassandraTestServer.Host, CassandraTestServer.Port, keyspace, Model,
            [typeof(SchemaSetupMigration).Assembly], services =>
            {
                if (serial is not { } level)
                {
                    return;
                }

                services.Replace(ServiceDescriptor.Singleton<ISession>(_ =>
                {
                    var cluster = Cluster.Builder()
                        .AddContactPoints(CassandraTestServer.Host)
                        .WithPort(CassandraTestServer.Port)
                        .WithQueryOptions(new QueryOptions().SetSerialConsistencyLevel(level))
                        .Build();
                    var session = cluster.Connect();
                    session.Execute($"CREATE KEYSPACE IF NOT EXISTS {keyspace} WITH replication = {{'class':'SimpleStrategy','replication_factor':1}}");
                    session.ChangeKeyspace(keyspace);
                    return session;
                }));
            });
        await db.Resolve<IMigrationExecutor>().ApplyAsync(new MigrationBuilder()
            .For<Voucher>(table => table.EnsureCollection())
            .For<QueuedJob>(table => table.EnsureCollection())
            .For<ItemType>(table => table.EnsureCollection())
            .For<Ledger>(table => table.EnsureCollection())
            .Operations);
        return db;
    }

    /// <summary>Runs one of each write that takes a lightweight transaction or reads at the serial consistency.</summary>
    private static async Task WriteEveryKindAsync(CassandraTestDatabase db)
    {
        using var scope = db.NewScope();
        T Resolve<T>() where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

        // A sequence (its read and its compare-and-set), inserts that skip duplicates, and their read back.
        var vouchers = new[] { new Voucher { Note = "a" }, new Voucher { Note = "b" } };
        await Resolve<IUpsertRepository<Voucher, Guid>>().InsertManyAsync(vouchers);

        // An upsert that inserts, and one that finds the key taken and updates.
        var voucher = await Resolve<IUpsertRepository<Voucher, Guid>>().UpsertAsync(new Voucher { Note = "c" });
        await Resolve<IUpsertRepository<Voucher, Guid>>().UpsertAsync(new Voucher { Id = voucher.Id, Note = "d" });

        // A conditional set-based write (the rows read at the serial consistency, each written IF the condition holds).
        var job = new QueuedJob { Status = "queued" };
        await Resolve<IAsyncRepository<QueuedJob, Guid>>().AddAsync(job);
        await Resolve<IUnitOfWork>().CommitAsync();
        await Resolve<IAsyncRepository<QueuedJob, Guid>>().UpdateManyAsync(x => x.Id == job.Id && x.Status == "queued",
            x => new QueuedJob { Status = "running" });

        // A value computed from the row.
        var itemType = new ItemType { NextSequence = 1 };
        await Resolve<IAsyncRepository<ItemType, Guid>>().AddAsync(itemType);
        await Resolve<IUnitOfWork>().CommitAsync();
        await Resolve<IReturningUpdateRepository<ItemType, Guid>>().UpdateAndGetAsync(x => x.Id == itemType.Id,
            x => new ItemType { NextSequence = x.NextSequence + 1 });

        // The token writes of a commit: the insert, then the update on the version read.
        var ledger = new Ledger { Holder = "ana", Balance = 1m };
        await Resolve<IAsyncRepository<Ledger, Guid>>().AddAsync(ledger);
        await Resolve<IUnitOfWork>().CommitAsync();
        ledger.Balance = 2m;
        await Resolve<IAsyncRepository<Ledger, Guid>>().ModifyAsync(ledger);
        await Resolve<IUnitOfWork>().CommitAsync();

        // A named lock's lease: taken, then released.
        await using (await Resolve<ILockingUnitOfWork>().AcquireLockAsync("serial-" + Guid.NewGuid().ToString("N"), TimeSpan.FromSeconds(10)))
        {
        }
    }

    private static readonly string[] Tables =
        ["vouchers", "queued_jobs", "item_types", "ledgers", CassandraSequences.TableName, CassandraNamedLocks.TableName];

    private static void AssertEveryTransactionAndSerialReadAt(ExecutedCql executed, ConsistencyLevel serial)
    {
        var statements = executed.Statements.Where(statement => Tables.Any(statement.Names)).ToList();
        var transactions = statements.Where(statement => statement.IsConditional).ToList();
        var serialReads = statements.Where(statement => statement.IsRead
                                                        && statement.Consistency is nameof(ConsistencyLevel.Serial) or nameof(ConsistencyLevel.LocalSerial))
            .ToList();

        Assert.That(transactions.Select(statement => statement.Cql), Has.Some.Contains(CassandraSequences.TableName)
            .And.Some.Contains(CassandraNamedLocks.TableName).And.Some.Contains("ledgers").And.Some.Contains("item_types"),
            "every kind of transaction ran");
        Assert.That(transactions.Select(statement => (statement.Cql, statement.SerialConsistency)),
            Has.All.Matches<(string Cql, string? Serial)>(statement => statement.Serial == serial.ToString()),
            $"every lightweight transaction is pinned to {serial}");
        Assert.That(serialReads.Select(statement => statement.Cql), Has.Some.Contains(CassandraSequences.TableName).And.Some.Contains("queued_jobs"),
            "the sequence and the conditional write read at the serial consistency");
        Assert.That(serialReads.Select(statement => (statement.Cql, statement.Consistency)),
            Has.All.Matches<(string Cql, string? Consistency)>(statement => statement.Consistency == serial.ToString()),
            $"every serial read runs at {serial}");
    }

    [Test]
    public async Task By_default_every_lightweight_transaction_and_serial_read_runs_at_SERIAL()
    {
        using var db = await OpenAsync(null);
        using var executed = new ExecutedCql();

        await WriteEveryKindAsync(db);

        AssertEveryTransactionAndSerialReadAt(executed, ConsistencyLevel.Serial);
    }

    /// <remarks>
    ///     <c>LOCAL_SERIAL</c> runs Paxos among the local data center's replicas only: two data centers can each win a
    ///     compare-and-set of the same row. A session built for it is the operator's choice for a keyspace every writer of a
    ///     row reaches through one data center, and the provider follows it everywhere.
    /// </remarks>
    [Test]
    public async Task A_session_built_for_LOCAL_SERIAL_runs_every_lightweight_transaction_and_serial_read_at_it()
    {
        using var db = await OpenAsync(ConsistencyLevel.LocalSerial);
        using var executed = new ExecutedCql();

        await WriteEveryKindAsync(db);

        AssertEveryTransactionAndSerialReadAt(executed, ConsistencyLevel.LocalSerial);
    }
}
