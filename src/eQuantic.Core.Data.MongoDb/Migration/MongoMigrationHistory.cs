using eQuantic.Core.Data.Migration;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb.Migration;

/// <summary>
///     Tracks applied migrations in a dedicated MongoDB collection (<c>_migrations</c> by default), keyed by the
///     migration's stable <see cref="MigrationAttribute.Id" />. The collection makes migration state visible and
///     queryable like any other data.
/// </summary>
public sealed class MongoMigrationHistory : IMigrationHistory
{
    /// <summary>The default history collection name.</summary>
    public const string DefaultCollectionName = "_migrations";

    private readonly IMongoDatabase _database;
    private readonly string _collectionName;

    /// <summary>Initializes the history over a database.</summary>
    /// <param name="database">The database.</param>
    /// <param name="collectionName">The history collection name; defaults to <see cref="DefaultCollectionName" />.</param>
    public MongoMigrationHistory(IMongoDatabase database, string? collectionName = null)
    {
        _database = database;
        _collectionName = collectionName ?? DefaultCollectionName;
    }

    private IMongoCollection<AppliedMigrationDocument> Collection =>
        _database.GetCollection<AppliedMigrationDocument>(_collectionName);

    /// <inheritdoc />
    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        var options = new ListCollectionNamesOptions { Filter = Builders<BsonDocument>.Filter.Eq("name", _collectionName) };
        using var cursor = await _database.ListCollectionNamesAsync(options, cancellationToken).ConfigureAwait(false);
        if (!await cursor.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            await _database.CreateCollectionAsync(_collectionName, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<string>> GetAppliedIdsAsync(CancellationToken cancellationToken = default)
    {
        return await Collection
            .Find(FilterDefinition<AppliedMigrationDocument>.Empty)
            .Project(document => document.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RecordAsync(AppliedMigration migration, CancellationToken cancellationToken = default)
    {
        var document = new AppliedMigrationDocument
        {
            Id = migration.Id,
            Title = migration.Title,
            Date = migration.Date,
            AppliedAt = migration.AppliedAt,
        };

        await Collection.InsertOneAsync(document, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The stored shape of an applied migration (keyed by its stable id).</summary>
    private sealed class AppliedMigrationDocument
    {
        [BsonId]
        public string Id { get; set; } = default!;

        public string Title { get; set; } = default!;

        public DateTime Date { get; set; }

        public DateTime AppliedAt { get; set; }
    }
}
