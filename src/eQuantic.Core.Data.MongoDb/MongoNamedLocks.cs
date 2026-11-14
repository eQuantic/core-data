using System.Runtime.CompilerServices;
using eQuantic.Core.Data.Repository.Locking;
using MongoDB.Bson;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb;

/// <summary>
///     Named locks on MongoDB, held as leases: one document per held name in the <see cref="CollectionName" />
///     collection, <c>{ _id: name, owner, expiresAt }</c>. A lease is written by one atomic <c>findOneAndUpdate</c>
///     upsert on the <c>_id</c>, whose update pipeline keeps the owner and expiry already there unless
///     <c>expiresAt &lt;= $$NOW</c>: an absent document is inserted with the new owner, an expired one is taken over, and
///     a live one is left as it is, so the document it answers names the holder. Two first writers racing to insert
///     collide on the unique <c>_id</c>; the loser is answered "held". The expiry is the <b>server's</b> clock
///     (<c>$$NOW</c>) plus the lease's duration, so the holders' clocks do not matter. The holder renews it with an
///     update conditioned on its owner, and deletes it the same way on release.
///     A TTL index on <c>expiresAt</c> sweeps the leases dead holders leave behind; it is housekeeping only, since an
///     expired lease is taken over whether or not the sweep has run. Lease writes never join the unit of work's
///     transaction: another holder has to see them while it is open.
/// </summary>
public static class MongoNamedLocks
{
    /// <summary>The collection holding the leases, one document per held name.</summary>
    public const string CollectionName = "_locks";

    /// <summary>The name of the TTL index on <c>expiresAt</c>.</summary>
    public const string ExpiryIndexName = "expiresAt_ttl";

    private static readonly ConditionalWeakTable<IMongoDatabase, Lazy<Task>> Ensured = new();

    /// <summary>The leases of a database's named locks.</summary>
    internal static LeaseStore Store(IMongoDatabase database) => new MongoLeaseStore(database);

    /// <summary>Creates the TTL index once per database (a failed creation is tried again next time).</summary>
    internal static async Task EnsureIndexAsync(IMongoDatabase database, IMongoCollection<BsonDocument> leases)
    {
        var ensure = Ensured.GetValue(database, _ => new Lazy<Task>(() => leases.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("expiresAt"),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero, Name = ExpiryIndexName }))));
        try
        {
            await ensure.Value.ConfigureAwait(false);
        }
        catch
        {
            Ensured.Remove(database);
            throw;
        }
    }

    /// <summary>The <c>$set</c> stage that pushes the expiry out on the server's clock.</summary>
    internal static PipelineUpdateDefinition<BsonDocument> Stamp(TimeSpan duration) =>
        new(PipelineDefinition<BsonDocument, BsonDocument>.Create(new BsonDocument("$set",
            new BsonDocument("expiresAt", new BsonDocument("$add", new BsonArray { "$$NOW", (long)Math.Ceiling(duration.TotalMilliseconds) })))));

    /// <summary>A value written as itself in a pipeline stage (a string starting with <c>$</c> would read as a path).</summary>
    internal static BsonDocument Literal(string value) => new("$literal", value);
}

/// <summary>The leases in a database's <see cref="MongoNamedLocks.CollectionName" /> collection.</summary>
internal sealed class MongoLeaseStore(IMongoDatabase database) : LeaseStore
{
    private readonly IMongoCollection<BsonDocument> _leases = database.GetCollection<BsonDocument>(MongoNamedLocks.CollectionName);

    public override async Task<Lease?> TryTakeAsync(string name, string owner, TimeSpan duration)
    {
        await MongoNamedLocks.EnsureIndexAsync(database, _leases).ConfigureAwait(false);

        // One stage, so both fields read the document as it was: free (absent or expired) takes the new owner and
        // expiry, live keeps the holder's. A missing expiresAt sorts before any date, so an absent lease reads as free.
        var free = new BsonDocument("$lte", new BsonArray { "$expiresAt", "$$NOW" });
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
                }).ConfigureAwait(false);
            return lease is not null && lease.GetValue("owner", BsonNull.Value) == owner ? new MongoLease(_leases, name, owner) : null;
        }
        catch (MongoCommandException exception) when (exception.Code == 11000)
        {
            return null; // Two first writers raced to insert it, and the other one did.
        }
    }
}

/// <summary>A lease this process holds in the leases collection.</summary>
internal sealed class MongoLease(IMongoCollection<BsonDocument> leases, string name, string owner) : Lease
{
    private BsonDocument Mine => new() { { "_id", name }, { "owner", owner } };

    public override async Task<bool> RenewAsync(TimeSpan duration)
    {
        var result = await leases.UpdateOneAsync(Mine, MongoNamedLocks.Stamp(duration)).ConfigureAwait(false);
        return result.MatchedCount > 0;
    }

    public override Task ReleaseAsync() => leases.DeleteOneAsync(Mine);
}
