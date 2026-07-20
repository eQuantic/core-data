using Microsoft.Azure.Cosmos;

namespace eQuantic.Core.Data.CosmosDb.Repository;

/// <summary>
///     The ready-to-use Cosmos unit of work. Register a custom subclass of <see cref="CosmosUnitOfWork" /> instead
///     only when you need to override behaviour.
/// </summary>
/// <param name="serviceProvider">The service provider (used to resolve repositories).</param>
/// <param name="client">The Cosmos client.</param>
/// <param name="database">The target database.</param>
/// <param name="model">The Cosmos model (container names, partition keys and ids per entity).</param>
public sealed class CosmosDefaultUnitOfWork(IServiceProvider serviceProvider, CosmosClient client, Database database, CosmosModel model)
    : CosmosUnitOfWork(serviceProvider, client, database, model);
