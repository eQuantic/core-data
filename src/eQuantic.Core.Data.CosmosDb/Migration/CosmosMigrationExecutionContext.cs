using eQuantic.Core.Data.Migration;
using Microsoft.Azure.Cosmos;

namespace eQuantic.Core.Data.CosmosDb.Migration;

/// <summary>
///     The Cosmos execution context handed to a <see cref="IMigrationBuilder.Run" /> escape hatch: it exposes the
///     target <see cref="Database" /> so a migration can do anything the SDK allows when the fluent operations
///     are not enough.
/// </summary>
/// <param name="database">The target database.</param>
public sealed class CosmosMigrationExecutionContext(Database database) : IMigrationExecutionContext
{
    /// <summary>The target database.</summary>
    public Database Database { get; } = database;
}

/// <summary>Convenience access to the Cosmos handle from the provider-agnostic <see cref="IMigrationExecutionContext" />.</summary>
public static class CosmosMigrationExecutionContextExtensions
{
    /// <summary>Narrows the context to Cosmos (e.g. <c>ctx.AsCosmos().Database</c>).</summary>
    /// <param name="context">The provider-agnostic context.</param>
    /// <returns>The Cosmos context.</returns>
    public static CosmosMigrationExecutionContext AsCosmos(this IMigrationExecutionContext context) =>
        (CosmosMigrationExecutionContext)context;
}
