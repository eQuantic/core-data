using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb;

/// <summary>
///     What the store assigns a store-generated member on MongoDB. A sequence is an atomic counter: one document per
///     collection and element in the <see cref="CollectionName" /> collection, advanced with <c>findOneAndUpdate</c>
///     and <c>$inc</c> (upserted on first use) and acknowledged by a majority of the replica set, so no two inserts ever
///     take the same number, a failover included (a <c>w:1</c> acknowledgement can be rolled back with the primary that
///     gave it, and the number handed out again). It advances outside the unit of work's transaction, as a PostgreSQL
///     sequence does: a transaction that rolls back leaves the numbers it took unused, a gap, and concurrent
///     transactions never wait on each other's counter. The clock is the server's (<c>hello</c>'s <c>localTime</c>),
///     read once per commit and given to every insert of it, the way PostgreSQL's <c>now()</c> is the transaction's time.
/// </summary>
public static class MongoSequences
{
    /// <summary>The collection holding the counters, one document per <c>&lt;collection&gt;.&lt;element&gt;</c>.</summary>
    public const string CollectionName = "_sequences";

    /// <summary>The counter document's <c>_id</c> for an element of a collection.</summary>
    /// <param name="collection">The entity's collection.</param>
    /// <param name="element">The member's stored element name.</param>
    public static string CounterId(string collection, string element) => $"{collection}.{element}";

    /// <summary>
    ///     Advances a counter by <paramref name="count" /> in one atomic step and returns its new value: the numbers
    ///     <c>value - count + 1</c> through <c>value</c> are the caller's, and nobody else's.
    /// </summary>
    internal static async Task<long> AdvanceAsync(IMongoDatabase database, string counterId, long count,
        CancellationToken cancellationToken)
    {
        var counters = Counters(database);
        var filter = Builders<BsonDocument>.Filter.Eq("_id", counterId);
        var update = Builders<BsonDocument>.Update.Inc("value", count);
        var options = new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After };

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // No session: the counter never joins the caller's transaction (see the type's remarks).
                var counter = await counters.FindOneAndUpdateAsync(filter, update, options, cancellationToken).ConfigureAwait(false);
                return counter["value"].ToInt64();
            }
            catch (MongoCommandException exception) when (exception.Code == 11000 && attempt == 0)
            {
                // Two first uses raced to upsert the counter and this one lost the insert: the counter exists now,
                // and the next $inc advances it.
            }
        }
    }

    /// <summary>
    ///     The counters' collection, written with a majority write concern: the number a <c>$inc</c> answers is one a
    ///     majority of the replica set holds, and no primary elected after a failover hands it out again.
    /// </summary>
    internal static IMongoCollection<BsonDocument> Counters(IMongoDatabase database) =>
        database.GetCollection<BsonDocument>(CollectionName).WithWriteConcern(WriteConcern.WMajority);

    /// <summary>The server's clock, in UTC, from <c>hello</c>'s <c>localTime</c>.</summary>
    internal static async Task<DateTime> ServerClockAsync(IMongoDatabase database, CancellationToken cancellationToken)
    {
        var reply = await database.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return reply["localTime"].ToUniversalTime();
    }
}

/// <summary>
///     A replace that keeps what the store generated: one pipeline update that replaces the document with the
///     entity's, while every store-generated element takes the value the stored document already holds
///     (<c>$replaceWith</c> over <c>$mergeObjects</c> of the entity as a <c>$literal</c> and the stored elements). One
///     atomic server-side write, with the replace's own meaning for every other element. The entity is serialized
///     when the write is rendered, at commit, like an insert's.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
internal sealed class KeepStoreGeneratedReplace<TEntity>(TEntity entity, IReadOnlyList<string> generatedElements)
    : UpdateDefinition<TEntity>
{
    public override BsonValue Render(RenderArgs<TEntity> args)
    {
        var document = new BsonDocument();
        using (var writer = new BsonDocumentWriter(document))
        {
            args.DocumentSerializer.Serialize(BsonSerializationContext.CreateRoot(writer), entity);
        }

        var stored = new BsonDocument();
        foreach (var element in generatedElements)
        {
            document.Remove(element);
            stored.Add(element, "$" + element);
        }

        return new BsonArray
        {
            new BsonDocument("$replaceWith",
                new BsonDocument("$mergeObjects", new BsonArray { new BsonDocument("$literal", document), stored })),
        };
    }
}
