using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb.Repository;

/// <summary>
///     A collection's staged writes. The unit of work accumulates one of these per entity type and flushes
///     each as a single ordered <c>BulkWrite</c> on commit — no per-entity change tracking, snapshotting or
///     change detection: the write model is captured straight from the caller's explicit intent
///     (<c>Add</c>/<c>Modify</c>/<c>Remove</c>).
/// </summary>
internal interface IPendingCollectionWrites
{
    /// <summary>Gets the number of staged operations.</summary>
    int Count { get; }

    /// <summary>Flushes the staged operations as one bulk write, optionally inside a transaction session.</summary>
    /// <param name="session">The active transaction session, or <c>null</c> to write directly.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of documents inserted, replaced, deleted or upserted.</returns>
    Task<long> FlushAsync(IClientSessionHandle? session, CancellationToken cancellationToken);

    /// <summary>Discards the staged operations without writing.</summary>
    void Clear();
}

/// <summary>The staged writes for a single <typeparamref name="TEntity" /> collection.</summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
internal sealed class PendingCollectionWrites<TEntity>(IMongoCollection<TEntity> collection)
    : IPendingCollectionWrites
    where TEntity : class
{
    private readonly List<WriteModel<TEntity>> _models = [];

    public int Count => _models.Count;

    public void Add(WriteModel<TEntity> model) => _models.Add(model);

    public void Clear() => _models.Clear();

    public async Task<long> FlushAsync(IClientSessionHandle? session, CancellationToken cancellationToken)
    {
        if (_models.Count == 0)
        {
            return 0;
        }

        var options = new BulkWriteOptions { IsOrdered = true };
        var result = session is null
            ? await collection.BulkWriteAsync(_models, options, cancellationToken).ConfigureAwait(false)
            : await collection.BulkWriteAsync(session, _models, options, cancellationToken).ConfigureAwait(false);

        _models.Clear();
        return result.InsertedCount + result.ModifiedCount + result.DeletedCount + result.Upserts.Count;
    }
}
