using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace eQuantic.Core.Data.CosmosDb;

/// <summary>
///     What the store assigns a store-generated member on Cosmos DB. A sequence is an atomic server-side counter: one
///     item per container and property in the <see cref="ContainerName" /> container (partitioned by <c>/id</c>,
///     created on first use), advanced by a <c>Patch</c> <c>Increment</c>, which answers the item as it now is — so no
///     two inserts ever take the same number. The increment runs outside a transactional batch (a batch is bound to
///     one container and one partition): a batch that fails leaves a gap, never a repeat, as a relational sequence
///     does. The clock is the server's (<c>SELECT VALUE GetCurrentDateTime()</c>), read once per commit and given to
///     every insert of it. Everything here goes through the stream API, whatever serializer the client carries.
///     <para>
///         The increment is atomic in the region that applies it. An account that accepts writes in several regions
///         applies it to each region's copy of the counter before the copies converge, so two inserts in two regions
///         could take the same number: the counters refuse such an account with <see cref="NotSupportedException" />
///         (its writable regions are read once per account and process).
///     </para>
/// </summary>
public static class CosmosSequences
{
    /// <summary>The container holding the counters, one item per <c>&lt;container&gt;.&lt;property&gt;</c>.</summary>
    public const string ContainerName = "_sequences";

    /// <summary>Why the counters cannot live in an account with several write regions.</summary>
    private const string Purpose =
        "A store-generated sequence advances its counter in '" + ContainerName + "' with a patch that each write region applies to " +
        "its own copy of the counter, so two inserts in two regions could take the same number.";

    /// <summary>The counter item's id for a property of a container's documents.</summary>
    /// <param name="container">The entity's container.</param>
    /// <param name="property">The member's stored property name.</param>
    public static string CounterId(string container, string property) => $"{container}.{property}";

    /// <summary>
    ///     Advances a counter by <paramref name="count" /> in one atomic step and returns its new value: the numbers
    ///     <c>value - count + 1</c> through <c>value</c> are the caller's, and nobody else's.
    /// </summary>
    internal static async Task<long> AdvanceAsync(Database database, string counterId, long count, CancellationToken cancellationToken)
    {
        var container = await CosmosSystemContainers.GetAsync(database, ContainerName, Purpose, cancellationToken).ConfigureAwait(false);
        var partitionKey = new PartitionKey(counterId);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using (var patched = await container.PatchItemStreamAsync(counterId, partitionKey,
                       [PatchOperation.Increment("/value", count)], cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (patched.IsSuccessStatusCode)
                {
                    using var item = await JsonDocument.ParseAsync(patched.Content, cancellationToken: cancellationToken).ConfigureAwait(false);
                    return item.RootElement.GetProperty("value").GetInt64();
                }

                if (CosmosSystemContainers.IsGone(patched))
                {
                    // The container was removed after this process created it: create it again, and the counter in it.
                    container = await CosmosSystemContainers.RecreateAsync(database, ContainerName, Purpose, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (patched.StatusCode != HttpStatusCode.NotFound)
                {
                    patched.EnsureSuccessStatusCode();
                }
            }

            // The counter's first use: create it at zero (a racing first use may have created it already) and
            // increment again.
            using var created = await container.CreateItemStreamAsync(
                new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object> { ["id"] = counterId, ["value"] = 0L }))),
                partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (CosmosSystemContainers.IsGone(created))
            {
                container = await CosmosSystemContainers.RecreateAsync(database, ContainerName, Purpose, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!created.IsSuccessStatusCode && created.StatusCode != HttpStatusCode.Conflict)
            {
                created.EnsureSuccessStatusCode();
            }
        }

        throw new InvalidOperationException($"The counter '{counterId}' in '{ContainerName}' could not be advanced.");
    }

    /// <summary>The server's clock, in UTC, read by a query scoped to one partition of a container (one answer).</summary>
    internal static async Task<DateTime> ServerClockAsync(Container container, PartitionKey partitionKey, CancellationToken cancellationToken)
    {
        using var iterator = container.GetItemQueryStreamIterator(new QueryDefinition("SELECT VALUE GetCurrentDateTime()"),
            requestOptions: new QueryRequestOptions { PartitionKey = partitionKey });
        while (iterator.HasMoreResults)
        {
            using var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            page.EnsureSuccessStatusCode();
            using var body = await JsonDocument.ParseAsync(page.Content, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var value in body.RootElement.GetProperty("Documents").EnumerateArray())
            {
                return DateTime.Parse(value.GetString()!, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            }
        }

        throw new InvalidOperationException("GetCurrentDateTime() answered nothing; the server's clock could not be read.");
    }
}
