using eQuantic.Core.Data.Migration;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb.Migration;

/// <summary>
///     The MongoDB execution context handed to a <see cref="IMigrationBuilder.Run" /> escape hatch: it exposes
///     the target <see cref="IMongoDatabase" /> so a migration can do anything the driver allows when the fluent
///     operations are not enough.
/// </summary>
/// <param name="database">The target database.</param>
public sealed class MongoMigrationExecutionContext(IMongoDatabase database) : IMigrationExecutionContext
{
    /// <summary>The target database.</summary>
    public IMongoDatabase Database { get; } = database;
}

/// <summary>Convenience access to the MongoDB handle from the provider-agnostic <see cref="IMigrationExecutionContext" />.</summary>
public static class MongoMigrationExecutionContextExtensions
{
    /// <summary>Narrows the context to MongoDB (e.g. <c>ctx.AsMongo().Database</c>).</summary>
    /// <param name="context">The provider-agnostic context.</param>
    /// <returns>The MongoDB context.</returns>
    public static MongoMigrationExecutionContext AsMongo(this IMigrationExecutionContext context) =>
        (MongoMigrationExecutionContext)context;
}
