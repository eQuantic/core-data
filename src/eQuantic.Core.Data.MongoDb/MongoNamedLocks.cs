using System.Runtime.CompilerServices;
using eQuantic.Core.Data.Diagnostics;
using eQuantic.Core.Data.Repository.Locking;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb;

/// <summary>
///     Named locks on MongoDB, held as leases: one document per held name in the <see cref="CollectionName" />
///     collection, <c>{ _id: name, owner, expiresAt }</c>. A lease is written by one atomic <c>findOneAndUpdate</c>
///     upsert on the <c>_id</c>, whose update pipeline keeps the owner and expiry already there unless
///     <c>expiresAt &lt;= $$NOW</c> or the owner is the writer itself: an absent document is inserted with the new
///     owner, an expired one is taken over, the writer's own (a write of the same acquisition whose answer was lost) is
///     written again, and another owner's live one is left as it is, so the document it answers names the holder. Two
///     first writers racing to insert collide on the unique <c>_id</c>; the loser is answered "held". The expiry is the
///     <b>server's</b> clock (<c>$$NOW</c>) plus the lease's duration, so the holders' clocks do not matter. The holder
///     renews it with an update conditioned on its owner, and deletes it the same way on release.
///     A TTL index on <c>expiresAt</c> sweeps the leases dead holders leave behind. It is housekeeping only, created
///     best effort: an expired lease is taken over whether or not the sweep has run, so an index that cannot be
///     created is logged (<see cref="DataEvents.LeaseSweepFailed" />) and the locks go on. Lease writes never join the
///     unit of work's transaction: another holder has to see them while it is open.
/// </summary>
public static class MongoNamedLocks
{
    /// <summary>The collection holding the leases, one document per held name.</summary>
    public const string CollectionName = "_locks";

    /// <summary>The name of the TTL index on <c>expiresAt</c>.</summary>
    public const string ExpiryIndexName = "expiresAt_ttl";

    private static readonly ConditionalWeakTable<IMongoDatabase, Lazy<Task<bool>>> Ensured = new();

    /// <summary>The leases of a database's named locks.</summary>
    /// <param name="database">The database holding the leases.</param>
    /// <param name="logger">Where an index that could not be created is reported, or <c>null</c>.</param>
    internal static LeaseStore Store(IMongoDatabase database, ILogger? logger = null) => new MongoLeaseStore(database, logger);

    /// <summary>
    ///     Creates the TTL index once per database, best effort: a refusal that will not change (an index on
    ///     <c>expiresAt</c> with other options is already there, or the user may not create indexes) is logged once, and
    ///     any other failure is logged and tried again next time. It never fails an acquisition.
    /// </summary>
    internal static async Task EnsureIndexAsync(IMongoDatabase database, IMongoCollection<BsonDocument> leases, ILogger? logger)
    {
        var ensure = Ensured.GetValue(database, _ => new Lazy<Task<bool>>(async () =>
        {
            try
            {
                await leases.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("expiresAt"),
                    new CreateIndexOptions { ExpireAfter = TimeSpan.Zero, Name = ExpiryIndexName })).ConfigureAwait(false);
                return true;
            }
            catch (Exception exception)
            {
                var settled = exception is MongoCommandException { Code: IndexOptionsConflict or IndexKeySpecsConflict or Unauthorized };
                logger?.Log(LogLevel.Warning, DataEvents.LeaseSweepFailed, exception,
                    "The TTL index on {Collection}.expiresAt could not be created; expired leases are still taken over, but nothing deletes the ones nobody takes",
                    CollectionName);
                return settled;
            }
        }));

        if (!await ensure.Value.ConfigureAwait(false))
        {
            Ensured.Remove(database);
        }
    }

    private const int Unauthorized = 13;
    private const int IndexOptionsConflict = 85;
    private const int IndexKeySpecsConflict = 86;

    /// <summary>The <c>$set</c> stage that pushes the expiry out on the server's clock.</summary>
    internal static PipelineUpdateDefinition<BsonDocument> Stamp(TimeSpan duration) =>
        new(PipelineDefinition<BsonDocument, BsonDocument>.Create(new BsonDocument("$set",
            new BsonDocument("expiresAt", new BsonDocument("$add", new BsonArray { "$$NOW", (long)Math.Ceiling(duration.TotalMilliseconds) })))));

    /// <summary>A value written as itself in a pipeline stage (a string starting with <c>$</c> would read as a path).</summary>
    internal static BsonDocument Literal(string value) => new("$literal", value);
}

/// <summary>The leases in a database's <see cref="MongoNamedLocks.CollectionName" /> collection.</summary>
internal sealed class MongoLeaseStore(IMongoDatabase database, ILogger? logger) : LeaseStore
{
    // Majority writes and reads: a lease a w:1 acknowledgement granted could be rolled back by a failover, and a new
    // primary would grant it to a second holder while the first still believes it holds it.
    private readonly IMongoCollection<BsonDocument> _leases = database.GetCollection<BsonDocument>(MongoNamedLocks.CollectionName)
        .WithWriteConcern(WriteConcern.WMajority).WithReadConcern(ReadConcern.Majority);

    public override async Task<Lease?> TryTakeAsync(string name, string owner, TimeSpan duration, CancellationToken cancellationToken)
    {
        await MongoNamedLocks.EnsureIndexAsync(database, _leases, logger).ConfigureAwait(false);

        // One stage, so both fields read the document as it was: free (absent, expired, or already this owner's)
        // takes the owner and a new expiry, another owner's live one keeps its holder's. A missing expiresAt sorts
        // before any date, so an absent lease reads as free.
        var free = new BsonDocument("$or", new BsonArray
        {
            new BsonDocument("$lte", new BsonArray { "$expiresAt", "$$NOW" }),
            new BsonDocument("$eq", new BsonArray { "$owner", MongoNamedLocks.Literal(owner) }),
        });
        var take = new BsonDocument
        {
            { "owner", new BsonDocument("$cond", new BsonArray { free, MongoNamedLocks.Literal(owner), "$owner" }) },
            {
                "expiresAt", new BsonDocument("$cond", new BsonArray
                {
                    free, new BsonDocument("$add", new BsonArray { "$$NOW", (long)Math.Ceiling(duration.TotalMilliseconds) }), "$expiresAt",
                })
            },
        };
        try
        {
            var lease = await _leases.FindOneAndUpdateAsync(new BsonDocument("_id", name),
                new PipelineUpdateDefinition<BsonDocument>(PipelineDefinition<BsonDocument, BsonDocument>.Create(new BsonDocument("$set", take))),
                new FindOneAndUpdateOptions<BsonDocument>
                {
                    IsUpsert = true,
                    ReturnDocument = ReturnDocument.After,
                    Projection = new BsonDocument("owner", 1),
                }, cancellationToken).ConfigureAwait(false);
            return lease is not null && lease.GetValue("owner", BsonNull.Value) == owner ? new MongoLease(_leases, name, owner) : null;
        }
        catch (MongoCommandException exception) when (exception.Code == 11000)
        {
            return null; // Two first writers raced to insert it, and the other one did.
        }
    }

    public override Task ReleaseAsync(string name, string owner, CancellationToken cancellationToken) =>
        _leases.DeleteOneAsync(new BsonDocument { { "_id", name }, { "owner", owner } }, cancellationToken);
}

/// <summary>A lease this process holds in the leases collection.</summary>
internal sealed class MongoLease(IMongoCollection<BsonDocument> leases, string name, string owner) : Lease
{
    private BsonDocument Mine => new() { { "_id", name }, { "owner", owner } };

    public override async Task<bool> RenewAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        var result = await leases.UpdateOneAsync(Mine, MongoNamedLocks.Stamp(duration), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return result.MatchedCount > 0;
    }

    public override Task ReleaseAsync(CancellationToken cancellationToken) => leases.DeleteOneAsync(Mine, cancellationToken);
}
