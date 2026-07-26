using eQuantic.Core.Data.Evolution;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb.Evolution;

/// <summary>
///     Describes both sides of a drift check for MongoDB — at the only level MongoDB has one.
///     <para>
///         A collection has no schema. No field can be compared: a document either carries a property or it does
///         not, and sampling documents would describe the ones that came back rather than the collection. So no
///         field is claimed here, and the check stays silent about them rather than guessing.
///     </para>
///     <para>
///         What a collection <em>does</em> have is the indexes the model asked for, and one of them is not about
///         speed at all. A time-to-live declaration is delivered as an index; without it nothing expires, and
///         documents that should have been deleted are still being read. That is worth looking for — it is the one
///         thing about a MongoDB collection that can silently stop being true.
///     </para>
/// </summary>
public sealed class MongoDatabaseSnapshotSource(MongoModel model, IMongoDatabase database) : IDatabaseSnapshotSource
{
    /// <inheritdoc />
    public string Provider => "mongodb";

    /// <inheritdoc />
    public DatabaseSnapshot Expect() =>
        new(Provider, model.EntityTypes.Select(Expect).ToList());

    /// <inheritdoc />
    public async Task<DatabaseSnapshot> ObserveAsync(CancellationToken cancellationToken = default)
    {
        var present = await NamesAsync(cancellationToken).ConfigureAwait(false);
        var collections = new List<DatabaseCollection>();

        foreach (var entityType in model.EntityTypes)
        {
            var name = MongoModeling.CollectionName(entityType);
            if (!present.Contains(name))
            {
                // Left out: the comparison reads its absence as a missing collection. MongoDB creates one on
                // write, so this is only ever a finding when the model expects indexes on it.
                continue;
            }

            var indexes = new List<DatabaseIndex>();
            using var cursor = await database.GetCollection<BsonDocument>(name).Indexes
                .ListAsync(cancellationToken).ConfigureAwait(false);

            foreach (var document in await cursor.ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                var indexName = document["name"].AsString;
                if (indexName == "_id_")
                {
                    // Every collection has it and no model declares it; reporting it would be noise.
                    continue;
                }

                indexes.Add(new DatabaseIndex(indexName, Keys(document["key"].AsBsonDocument))
                {
                    ExpiresDocuments = document.Contains("expireAfterSeconds"),
                });
            }

            collections.Add(new DatabaseCollection(name,
                entityType.FullName ?? entityType.Name, []) { Indexes = indexes });
        }

        return new DatabaseSnapshot(Provider, collections);
    }

    private static DatabaseCollection Expect(Type entityType)
    {
        var indexes = new List<DatabaseIndex>();

        if (MongoModeling.TimeToLive(entityType) is { } ttl &&
            entityType.GetProperty(ttl.Member) is { } member)
        {
            var element = MongoFieldNames.Resolve(entityType, member);
            indexes.Add(new DatabaseIndex($"ttl_{element}", $"{element}:1") { ExpiresDocuments = true });
        }

        var clustering = MongoModeling.ClusteringKeys(entityType);
        if (clustering.Count > 0)
        {
            var keys = clustering
                .Select(key => entityType.GetProperty(key.Member) is { } property
                    ? $"{MongoFieldNames.Resolve(entityType, property)}:{(key.Descending ? "-1" : "1")}"
                    : null)
                .Where(key => key is not null);
            indexes.Add(new DatabaseIndex("ix_clustering", string.Join(",", keys)));
        }

        return new DatabaseCollection(MongoModeling.CollectionName(entityType),
            entityType.FullName ?? entityType.Name, []) { Indexes = indexes };
    }

    /// <summary>The index's keys as one string, in declaration order — the order is part of what an index is.</summary>
    private static string Keys(BsonDocument key) =>
        string.Join(",", key.Elements.Select(element => $"{element.Name}:{element.Value.ToInt32()}"));

    private async Task<HashSet<string>> NamesAsync(CancellationToken cancellationToken)
    {
        using var cursor = await database.ListCollectionNamesAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return (await cursor.ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
    }
}
