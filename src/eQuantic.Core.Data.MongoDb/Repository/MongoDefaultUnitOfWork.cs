using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb.Repository;

/// <summary>
///     The ready-to-use MongoDB unit of work, resolving collection names from the entity type name. Register a
///     custom subclass of <see cref="MongoUnitOfWork" /> instead when you need a different naming convention.
/// </summary>
/// <param name="serviceProvider">The service provider (used to resolve repositories).</param>
/// <param name="client">The MongoDB client.</param>
/// <param name="database">The target database.</param>
public sealed class MongoDefaultUnitOfWork(IServiceProvider serviceProvider, IMongoClient client, IMongoDatabase database)
    : MongoUnitOfWork(serviceProvider, client, database);
