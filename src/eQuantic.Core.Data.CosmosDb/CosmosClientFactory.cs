using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace eQuantic.Core.Data.CosmosDb;

/// <summary>
///     Builds <see cref="CosmosClient" />s configured the way this provider expects: a System.Text.Json
///     serializer with web (camelCase) defaults — so an entity's <c>Id</c> maps to the Cosmos <c>id</c> field and
///     partition key paths are camelCase — and bulk execution enabled so a buffered <c>Commit</c> flushes as one
///     batched set of point writes.
/// </summary>
public static class CosmosClientFactory
{
    /// <summary>The System.Text.Json options used to (de)serialize documents: web defaults (camelCase).</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web);

    /// <summary>The default client options (System.Text.Json camelCase serializer, bulk execution enabled).</summary>
    public static CosmosClientOptions DefaultOptions() => new()
    {
        AllowBulkExecution = true,
        UseSystemTextJsonSerializerWithOptions = SerializerOptions,
    };

    /// <summary>Creates a client from a connection string with the default options.</summary>
    /// <param name="connectionString">The Cosmos connection string.</param>
    public static CosmosClient Create(string connectionString) => new(connectionString, DefaultOptions());
}
