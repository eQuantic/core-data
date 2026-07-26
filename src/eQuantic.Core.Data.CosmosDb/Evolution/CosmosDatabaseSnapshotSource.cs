using eQuantic.Core.Data.Evolution;
using Microsoft.Azure.Cosmos;

namespace eQuantic.Core.Data.CosmosDb.Evolution;

/// <summary>
///     Describes both sides of a drift check for Cosmos DB — at the level Cosmos actually has one.
///     <para>
///         A container holds no schema, so no field can be compared: a document either carries a property or it
///         does not, and reading a sample would only describe the documents that happened to come back. What
///         Cosmos <em>does</em> keep is the container itself and the partition key paths it was created with, and
///         both matter more than any field would. A missing container fails every request. A partition key that
///         differs cannot be changed at all — it is fixed for the container's life — so learning it here, rather
///         than from a deployment, is the difference between a rebuild you planned and one you didn't.
///     </para>
/// </summary>
public sealed class CosmosDatabaseSnapshotSource(CosmosModel model, Database database) : IDatabaseSnapshotSource
{
    /// <inheritdoc />
    public string Provider => "cosmosdb";

    /// <inheritdoc />
    public DatabaseSnapshot Expect() =>
        new(Provider, model.Configurations.Values
            .Select(configuration => new DatabaseCollection(
                configuration.ContainerName,
                configuration.EntityType.FullName ?? configuration.EntityType.Name,
                [])
            {
                PartitionKeys = configuration.PartitionKeyPaths,
            })
            .ToList());

    /// <inheritdoc />
    public async Task<DatabaseSnapshot> ObserveAsync(CancellationToken cancellationToken = default)
    {
        var collections = new List<DatabaseCollection>();

        foreach (var configuration in model.Configurations.Values)
        {
            var container = database.GetContainer(configuration.ContainerName);
            ContainerProperties properties;
            try
            {
                properties = (await container.ReadContainerAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false)).Resource;
            }
            catch (CosmosException failure) when (failure.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Left out entirely: the comparison reads its absence as a missing collection, which is what it is.
                continue;
            }

            collections.Add(new DatabaseCollection(configuration.ContainerName,
                configuration.EntityType.FullName ?? configuration.EntityType.Name, [])
            {
                // A hierarchical key reports its paths; a single key reports one. Either way these are the paths
                // the container was created with, and nothing can change them afterwards.
                PartitionKeys = properties.PartitionKeyPaths is { Count: > 0 } paths
                    ? paths.ToList()
                    : [properties.PartitionKeyPath],
            });
        }

        return new DatabaseSnapshot(Provider, collections);
    }
}
