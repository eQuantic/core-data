using System.Text.Json.Nodes;
using eQuantic.Core.Data.Migration;
using Microsoft.Azure.Cosmos;

namespace eQuantic.Core.Data.CosmosDb.Migration;

/// <summary>
///     Tracks applied migrations in a dedicated Cosmos container (<c>_migrations</c> by default), keyed and
///     partitioned by the migration's stable <see cref="MigrationAttribute.Id" />.
/// </summary>
public sealed class CosmosMigrationHistory : IMigrationHistory
{
    /// <summary>The default history container name.</summary>
    public const string DefaultContainerName = "_migrations";

    private readonly Database _database;
    private readonly string _containerName;

    /// <summary>Initializes the history over a database.</summary>
    /// <param name="database">The database.</param>
    /// <param name="containerName">The history container name; defaults to <see cref="DefaultContainerName" />.</param>
    public CosmosMigrationHistory(Database database, string? containerName = null)
    {
        _database = database;
        _containerName = containerName ?? DefaultContainerName;
    }

    private Container Container => _database.GetContainer(_containerName);

    /// <inheritdoc />
    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default) =>
        await _database.CreateContainerIfNotExistsAsync(new ContainerProperties(_containerName, "/id"), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<string>> GetAppliedIdsAsync(CancellationToken cancellationToken = default)
    {
        var ids = new List<string>();
        using var iterator = Container.GetItemQueryIterator<JsonObject>(new QueryDefinition("SELECT c.id FROM c"));
        while (iterator.HasMoreResults)
        {
            foreach (var document in await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(document["id"]!.GetValue<string>());
            }
        }

        return ids;
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

        await Container.UpsertItemAsync(document, new PartitionKey(migration.Id), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The stored shape of an applied migration (keyed by its stable id → the Cosmos <c>id</c>).</summary>
    private sealed class AppliedMigrationDocument
    {
        public string Id { get; set; } = default!;

        public string Title { get; set; } = default!;

        public DateTime Date { get; set; }

        public DateTime AppliedAt { get; set; }
    }
}
