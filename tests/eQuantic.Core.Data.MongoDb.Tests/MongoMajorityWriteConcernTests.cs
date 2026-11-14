using System.Collections.Concurrent;
using eQuantic.Core.Data.MongoDb.Repository;
using eQuantic.Core.Data.Repository;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>
///     The writes whose acknowledgement hands out something unique — a sequence's next number, a named lock's lease —
///     ask a majority of the replica set to acknowledge them, read off the commands the driver sends to a real MongoDB.
///     A <c>w:1</c> acknowledgement can be rolled back with the primary that gave it, after which a new primary hands the
///     number out again, or grants the lease to a second holder; a failover is not something a single-node test can
///     stage, so the test asserts what each command asks for.
/// </summary>
[TestFixture]
public sealed class MongoMajorityWriteConcernTests : MongoIntegrationTest
{
    private readonly ConcurrentQueue<BsonDocument> _commands = new();
    private MongoClient _client = null!;

    [SetUp]
    public void ConnectWatching()
    {
        _commands.Clear();
        var settings = MongoTestServer.Client.Settings.Clone();
        settings.ClusterConfigurator = cluster =>
            cluster.Subscribe<CommandStartedEvent>(started => _commands.Enqueue(started.Command.DeepClone().AsBsonDocument));
        _client = new MongoClient(settings);
    }

    [TearDown]
    public void Disconnect() => _client?.Dispose();

    private MongoTestDatabase NewWatchedDatabase(Action<IServiceCollection>? configure = null) =>
        new(_client, "test_" + Guid.NewGuid().ToString("N"), [], configure);

    private IReadOnlyList<BsonDocument> Sent(string command, string collection) =>
        _commands.Where(sent => sent.GetValue(command, BsonNull.Value) == collection).ToList();

    private static BsonValue WriteConcern(BsonDocument command) =>
        command.GetValue("writeConcern", BsonNull.Value) is BsonDocument concern ? concern.GetValue("w", BsonNull.Value) : BsonNull.Value;

    [Test]
    public async Task A_sequence_advances_with_a_majority_write_concern()
    {
        using var db = NewWatchedDatabase();
        await db.Resolve<IAsyncRepository<Ticket, string>>().AddAsync(new Ticket { Title = "numbered" });
        await db.Resolve<MongoDefaultUnitOfWork>().CommitAsync();

        var advances = Sent("findAndModify", MongoSequences.CollectionName);
        Assert.That(advances, Is.Not.Empty, "the commit advanced the counter");
        Assert.That(advances.Select(WriteConcern), Is.All.EqualTo(new BsonString("majority")));
    }

    [Test]
    public async Task A_sequence_advances_with_a_majority_write_concern_beside_an_open_transaction()
    {
        using var db = NewWatchedDatabase();
        var unitOfWork = db.Resolve<MongoDefaultUnitOfWork>();
        await unitOfWork.BeginTransactionAsync();
        await db.Resolve<IAsyncRepository<Ticket, string>>().AddAsync(new Ticket { Title = "numbered" });
        await unitOfWork.CommitAsync();
        await unitOfWork.CommitTransactionAsync();

        var advances = Sent("findAndModify", MongoSequences.CollectionName);
        Assert.That(advances, Is.Not.Empty);
        Assert.That(advances.Select(WriteConcern), Is.All.EqualTo(new BsonString("majority")),
            "outside the transaction, the counter keeps its own write concern");
        Assert.That(advances.Select(command => command.Contains("autocommit")), Is.All.False, "and never joins the transaction");
    }

    [Test]
    public async Task A_lease_is_taken_renewed_and_released_with_a_majority_write_concern()
    {
        using var db = NewWatchedDatabase(services => services.AddSingleton(new NamedLockOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(2),
            RenewalInterval = TimeSpan.FromMilliseconds(200),
        }));

        var held = await db.Resolve<ILockingUnitOfWork>().AcquireLockAsync("nightly-report", TimeSpan.FromSeconds(10));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Sent("update", MongoNamedLocks.CollectionName).Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        await held.DisposeAsync();

        var taken = Sent("findAndModify", MongoNamedLocks.CollectionName);
        var renewed = Sent("update", MongoNamedLocks.CollectionName);
        var released = Sent("delete", MongoNamedLocks.CollectionName);
        Assert.That(taken, Is.Not.Empty, "the lease was taken");
        Assert.That(renewed, Is.Not.Empty, "the lease was renewed while held");
        Assert.That(released, Is.Not.Empty, "the lease was released");
        Assert.That(taken.Concat(renewed).Concat(released).Select(WriteConcern), Is.All.EqualTo(new BsonString("majority")));
    }
}
