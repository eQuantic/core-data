using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.MongoDb.Repository;
using eQuantic.Core.Data.Repository;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>A ticket numbered by a sequence and stamped by the server's clock.</summary>
public sealed class Ticket : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = "";

    [StoreGenerated]
    public long Number { get; set; }

    [StoreGenerated]
    public DateTime OpenedAt { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>
///     The values a commit takes from the store, against a real MongoDB, when the commit fails before it writes and is
///     tried again, and when transactions insert at the same time: a retried commit assigns them again (never writes an
///     insert without them), and a counter advances outside the transactions, so two of them inserting into one
///     collection both commit, with numbers of their own.
/// </summary>
[TestFixture]
public sealed class MongoStoreGeneratedCommitTests : MongoIntegrationTest
{
    private const string Counter = "Ticket.Number";

    private MongoTestDatabase _db = null!;

    [SetUp]
    public void CreateDatabase() => _db = NewDatabase();

    [TearDown]
    public void DropDatabase() => _db?.Dispose();

    private static IAsyncRepository<Ticket, string> Tickets(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IAsyncRepository<Ticket, string>>();

    private static MongoDefaultUnitOfWork UnitOfWork(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<MongoDefaultUnitOfWork>();

    private Task<BsonDocument> Stored(string id) =>
        _db.Database.GetCollection<BsonDocument>(nameof(Ticket)).Find(new BsonDocument("_id", id)).SingleAsync();

    [Test]
    public async Task A_commit_whose_clock_read_failed_assigns_the_values_when_it_is_tried_again()
    {
        var before = DateTime.UtcNow.AddMinutes(-1);
        using var scope = _db.NewScope();
        var ticket = new Ticket { Title = "retried" };
        await Tickets(scope).AddAsync(ticket);

        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            Assert.That(async () => await UnitOfWork(scope).CommitAsync(cancelled.Token), Throws.InstanceOf<OperationCanceledException>(),
                "the commit stops reading the server's clock");
        }

        await UnitOfWork(scope).CommitAsync();

        var stored = await Stored(ticket.Id);
        Assert.That(stored["Number"].ToInt64(), Is.GreaterThan(0L), "the retried commit took a number from the counter");
        Assert.That(stored["OpenedAt"].ToUniversalTime(), Is.GreaterThan(before), "the retried commit read the server's clock");
        Assert.That(ticket.Number, Is.EqualTo(stored["Number"].ToInt64()), "and handed the number back on the entity");
    }

    [Test]
    public async Task A_commit_whose_counter_failed_assigns_the_values_when_it_is_tried_again()
    {
        // A counter MongoDB cannot $inc: the commit fails on it, after the clock was read.
        var counters = _db.Database.GetCollection<BsonDocument>(MongoSequences.CollectionName);
        await counters.InsertOneAsync(new BsonDocument { { "_id", Counter }, { "value", "not a number" } });

        using var scope = _db.NewScope();
        var ticket = new Ticket { Title = "retried" };
        await Tickets(scope).AddAsync(ticket);
        Assert.That(async () => await UnitOfWork(scope).CommitAsync(), Throws.InstanceOf<MongoException>(),
            "the commit stops on the counter it cannot advance");
        Assert.That(await _db.Database.GetCollection<BsonDocument>(nameof(Ticket)).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty),
            Is.Zero, "nothing was written");

        await counters.UpdateOneAsync(new BsonDocument("_id", Counter), new BsonDocument("$set", new BsonDocument("value", 41L)));
        await UnitOfWork(scope).CommitAsync();

        var stored = await Stored(ticket.Id);
        Assert.That(stored["Number"].ToInt64(), Is.EqualTo(42L), "the retried commit advanced the counter it failed on");
        Assert.That(stored["OpenedAt"].ToUniversalTime(), Is.GreaterThan(DateTime.UtcNow.AddMinutes(-1)));
    }

    [Test]
    public async Task Concurrent_transactions_inserting_into_one_collection_both_commit()
    {
        // The counter exists before the transactions start, so the race is over its $inc alone.
        using (var seed = _db.NewScope())
        {
            await Tickets(seed).AddAsync(new Ticket { Title = "seed" });
            await UnitOfWork(seed).CommitAsync();
        }

        using var first = _db.NewScope();
        using var second = _db.NewScope();
        var mine = new Ticket { Title = "first" };
        var theirs = new Ticket { Title = "second" };

        await UnitOfWork(first).BeginTransactionAsync();
        await Tickets(first).AddAsync(mine);
        await UnitOfWork(first).CommitAsync();

        // The first transaction is still open: a counter advanced inside it would be write-locked until it ends.
        await UnitOfWork(second).BeginTransactionAsync();
        await Tickets(second).AddAsync(theirs);
        await UnitOfWork(second).CommitAsync();

        await UnitOfWork(first).CommitTransactionAsync();
        await UnitOfWork(second).CommitTransactionAsync();

        var numbers = new[] { (await Stored(mine.Id))["Number"].ToInt64(), (await Stored(theirs.Id))["Number"].ToInt64() };
        Assert.That(numbers, Is.Unique, "each transaction took a number of its own");
        Assert.That(numbers, Is.All.GreaterThan(1L), "after the seed's");
    }
}
