using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb;

/// <summary>
///     What the store assigns a store-generated member on MongoDB. A sequence is an atomic counter: one document per
///     collection and element in the <see cref="CollectionName" /> collection, advanced with <c>findOneAndUpdate</c>
///     and <c>$inc</c> (upserted on first use), inside the unit of work's transaction when one is open — so no two
///     inserts ever take the same number. The clock is the server's (<c>hello</c>'s <c>localTime</c>), read once per
///     commit and given to every insert of it, the way PostgreSQL's <c>now()</c> is the transaction's time.
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
    internal static async Task<long> AdvanceAsync(IMongoDatabase database, IClientSessionHandle? session, string counterId,
        long count, CancellationToken cancellationToken)
    {
        var counters = database.GetCollection<BsonDocument>(CollectionName);
        var filter = Builders<BsonDocument>.Filter.Eq("_id", counterId);
        var update = Builders<BsonDocument>.Update.Inc("value", count);
        var options = new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After };

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var counter = session is null
                    ? await counters.FindOneAndUpdateAsync(filter, update, options, cancellationToken).ConfigureAwait(false)
                    : await counters.FindOneAndUpdateAsync(session, filter, update, options, cancellationToken).ConfigureAwait(false);
                return counter["value"].ToInt64();
            }
            catch (MongoCommandException exception) when (exception.Code == 11000 && attempt == 0 && session is not { IsInTransaction: true })
            {
                // Two first uses raced to upsert the counter and this one lost the insert: the counter exists now,
                // and the next $inc advances it.
            }
        }
    }

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
