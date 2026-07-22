using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace eQuantic.Core.Data.CosmosDb;

/// <summary>
///     Builds <see cref="CosmosClient" />s configured the way this provider expects: the
///     <see cref="CosmosEntitySerializer" /> (System.Text.Json web defaults + the modeling annotations, and the
///     model's <c>Converts</c> registrations when a model is given) — wired as a <see cref="CosmosLinqSerializer" />
///     so LINQ member names resolve through the same contract — and bulk execution enabled so a buffered
///     <c>Commit</c> flushes as one batched set of point writes.
/// </summary>
public static class CosmosClientFactory
{
    /// <summary>The System.Text.Json options used to (de)serialize documents when no model is supplied.</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = CosmosEntitySerializer.BuildOptions();

    /// <summary>The default client options (annotation-aware serializer, bulk execution enabled).</summary>
    public static CosmosClientOptions DefaultOptions() => new()
    {
        AllowBulkExecution = true,
        Serializer = new CosmosEntitySerializer(SerializerOptions),
    };

    /// <summary>The client options for a model (its <c>Converts</c> registrations join the serializer).</summary>
    /// <param name="model">The Cosmos model.</param>
    public static CosmosClientOptions OptionsFor(CosmosModel model) => new()
    {
        AllowBulkExecution = true,
        Serializer = new CosmosEntitySerializer(CosmosEntitySerializer.BuildOptions(model)),
    };

    /// <summary>Creates a client from a connection string with the default options.</summary>
    /// <param name="connectionString">The Cosmos connection string.</param>
    public static CosmosClient Create(string connectionString) => new(connectionString, DefaultOptions());

    /// <summary>Creates a client whose serializer carries the model's value converters.</summary>
    /// <param name="connectionString">The Cosmos connection string.</param>
    /// <param name="model">The Cosmos model.</param>
    public static CosmosClient Create(string connectionString, CosmosModel model) => new(connectionString, OptionsFor(model));
}
