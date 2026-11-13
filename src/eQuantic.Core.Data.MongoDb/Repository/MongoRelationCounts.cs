using MongoDB.Bson.Serialization;

namespace eQuantic.Core.Data.MongoDb.Repository;

/// <summary>
///     A document carried beside the relation counts a projection asked for: the entity as stored (<c>e</c>) and the
///     counts the <c>$lookup</c> + <c>$count</c> stages produced (<c>c</c>), so the projection reads both.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
internal sealed class MongoRelationCounts<TEntity>
{
    private static readonly Lazy<IBsonSerializer<MongoRelationCounts<TEntity>>> Lookup = new(() =>
    {
        BsonClassMap.TryRegisterClassMap<MongoRelationCounts<TEntity>>(map =>
        {
            map.MapProperty(carrier => carrier.Entity).SetElementName("e");
            map.MapProperty(carrier => carrier.Counts).SetElementName("c");
            map.SetIgnoreExtraElements(true);
        });
        return BsonSerializer.LookupSerializer<MongoRelationCounts<TEntity>>();
    });

    /// <summary>The entity.</summary>
    public TEntity Entity { get; set; } = default!;

    /// <summary>The counts, in the order the projection asked for them.</summary>
    public long[] Counts { get; set; } = [];

    /// <summary>The carrier's serializer (its class map registered on first use).</summary>
    public static IBsonSerializer<MongoRelationCounts<TEntity>> Serializer => Lookup.Value;
}
