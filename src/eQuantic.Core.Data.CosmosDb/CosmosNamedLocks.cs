using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using eQuantic.Core.Data.Repository.Locking;
using Microsoft.Azure.Cosmos;

namespace eQuantic.Core.Data.CosmosDb;

/// <summary>
///     Named locks on Cosmos DB, held as leases: one item per held name in the <see cref="ContainerName" /> container
///     (partitioned by <c>/id</c>, created on first use), <c>{ id, name, owner, leaseSeconds }</c>. A lease is written
///     by a create, which answers 409 while another owner's item is there. That item is taken over only once it has
///     expired by the <b>server's</b> clock: its <c>_ts</c> (the server's time of its last write, in whole seconds)
///     plus one second plus its lease is compared with <c>GetCurrentDateTime()</c>, and the takeover is a replace
///     guarded by the item's ETag, so of two owners racing for it one wins. The holder renews it with an ETag-guarded
///     replace (which moves <c>_ts</c>) and deletes it the same way on release; another owner's item is never touched.
///     Lease writes never join a transactional batch: another holder has to see them while it is pending.
///     <para>
///         The item's id is the name itself when it is at most 255 characters of printable ASCII without <c>/</c>,
///         <c>\</c>, <c>?</c> or <c>#</c> (which an id cannot hold); otherwise <c>~</c> and its SHA-256. The
///         <c>name</c> property keeps the name as it was given.
///     </para>
/// </summary>
public static class CosmosNamedLocks
{
    /// <summary>The container holding the leases, one item per held name.</summary>
    public const string ContainerName = "_locks";

    private const int MaxIdLength = 255;

    private static readonly ConcurrentDictionary<string, Lazy<Task>> Ensured = new();

    /// <summary>The item id a lock's name is held as.</summary>
    /// <param name="name">The lock's name.</param>
    public static string ItemId(string name) =>
        NamedLockKeys.Text(name, MaxIdLength, character => character is not ('/' or '\\' or '?' or '#'));

    /// <summary>The leases of a database's named locks.</summary>
    internal static LeaseStore Store(Database database) => new CosmosLeaseStore(database);

    /// <summary>The leases' container, created on first use (once per database and process).</summary>
    internal static async Task<Container> LeasesAsync(Database database)
    {
        var key = $"{database.Client.Endpoint}|{database.Id}";
        var ensure = Ensured.GetOrAdd(key, _ => new Lazy<Task>(() =>
            database.CreateContainerIfNotExistsAsync(new ContainerProperties(ContainerName, "/id"), cancellationToken: CancellationToken.None)));
        try
        {
            await ensure.Value.ConfigureAwait(false);
        }
        catch
        {
            // A failed creation is not remembered: the next use tries again.
            Ensured.TryRemove(new KeyValuePair<string, Lazy<Task>>(key, ensure));
            throw;
        }

        return database.GetContainer(ContainerName);
    }

    /// <summary>The lease item's body.</summary>
    internal static Stream Body(string id, string name, string owner, TimeSpan duration) =>
        new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id"] = id,
            ["name"] = name,
            ["owner"] = owner,
            ["leaseSeconds"] = Math.Ceiling(duration.TotalSeconds),
        })));

    /// <summary>A lease item as read: its owner, the server's time of its last write, and its lease.</summary>
    internal static async Task<(string Owner, long WrittenAt, double LeaseSeconds)> ReadAsync(Stream content)
    {
        using var item = await JsonDocument.ParseAsync(content).ConfigureAwait(false);
        var root = item.RootElement;
        return (root.GetProperty("owner").GetString() ?? string.Empty,
            root.GetProperty("_ts").GetInt64(),
            root.GetProperty("leaseSeconds").GetDouble());
    }
}

/// <summary>The leases in a database's <see cref="CosmosNamedLocks.ContainerName" /> container.</summary>
internal sealed class CosmosLeaseStore(Database database) : LeaseStore
{
    public override async Task<Lease?> TryTakeAsync(string name, string owner, TimeSpan duration)
    {
        var container = await CosmosNamedLocks.LeasesAsync(database).ConfigureAwait(false);
        var id = CosmosNamedLocks.ItemId(name);
        var partitionKey = new PartitionKey(id);

        using (var created = await container.CreateItemStreamAsync(CosmosNamedLocks.Body(id, name, owner, duration), partitionKey)
                   .ConfigureAwait(false))
        {
            if (created.IsSuccessStatusCode)
            {
                return new CosmosLease(container, id, name, owner, created.Headers.ETag);
            }

            if (created.StatusCode != HttpStatusCode.Conflict)
            {
                created.EnsureSuccessStatusCode();
            }
        }

        // Another owner's lease is there: it is taken over only once it has expired by the server's clock.
        using var read = await container.ReadItemStreamAsync(id, partitionKey).ConfigureAwait(false);
        if (read.StatusCode == HttpStatusCode.NotFound)
        {
            return null; // Released meanwhile: the next attempt creates it.
        }

        read.EnsureSuccessStatusCode();
        var (_, writtenAt, leaseSeconds) = await CosmosNamedLocks.ReadAsync(read.Content).ConfigureAwait(false);
        var now = await CosmosSequences.ServerClockAsync(container, partitionKey, CancellationToken.None).ConfigureAwait(false);

        // _ts counts whole seconds, so the write happened before _ts + 1: the lease is surely over past that plus its length.
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(writtenAt + 1).UtcDateTime.AddSeconds(leaseSeconds);
        if (now < expiresAt)
        {
            return null;
        }

        using var replaced = await container.ReplaceItemStreamAsync(CosmosNamedLocks.Body(id, name, owner, duration), id,
            partitionKey, new ItemRequestOptions { IfMatchEtag = read.Headers.ETag }).ConfigureAwait(false);
        if (replaced.IsSuccessStatusCode)
        {
            return new CosmosLease(container, id, name, owner, replaced.Headers.ETag);
        }

        if (replaced.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return null; // Another owner took it over (or released it) first.
        }

        replaced.EnsureSuccessStatusCode();
        return null;
    }
}

/// <summary>A lease this process holds, addressed by the ETag of its last write.</summary>
internal sealed class CosmosLease(Container container, string id, string name, string owner, string etag) : Lease
{
    private readonly PartitionKey _partitionKey = new(id);
    private string _etag = etag;

    public override async Task<bool> RenewAsync(TimeSpan duration)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var replaced = await container.ReplaceItemStreamAsync(CosmosNamedLocks.Body(id, name, owner, duration), id,
                _partitionKey, new ItemRequestOptions { IfMatchEtag = _etag }).ConfigureAwait(false);
            if (replaced.IsSuccessStatusCode)
            {
                _etag = replaced.Headers.ETag;
                return true;
            }

            if (replaced.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            if (replaced.StatusCode != HttpStatusCode.PreconditionFailed)
            {
                throw Unexpected(replaced);
            }

            // The ETag moved: a renewal whose answer was lost, or another owner's takeover. Only the item says which.
            if (await CurrentEtagIfMineAsync().ConfigureAwait(false) is not { } current)
            {
                return false;
            }

            _etag = current;
        }

        return false;
    }

    public override async Task ReleaseAsync()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var deleted = await container.DeleteItemStreamAsync(id, _partitionKey,
                new ItemRequestOptions { IfMatchEtag = _etag }).ConfigureAwait(false);
            if (deleted.IsSuccessStatusCode || deleted.StatusCode == HttpStatusCode.NotFound)
            {
                return;
            }

            if (deleted.StatusCode != HttpStatusCode.PreconditionFailed)
            {
                throw Unexpected(deleted);
            }

            if (await CurrentEtagIfMineAsync().ConfigureAwait(false) is not { } current)
            {
                return; // Another owner's now: left alone.
            }

            _etag = current;
        }
    }

    /// <summary>The item's current ETag when it is still this owner's, or <c>null</c>.</summary>
    private async Task<string?> CurrentEtagIfMineAsync()
    {
        using var read = await container.ReadItemStreamAsync(id, _partitionKey).ConfigureAwait(false);
        if (read.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        read.EnsureSuccessStatusCode();
        var (current, _, _) = await CosmosNamedLocks.ReadAsync(read.Content).ConfigureAwait(false);
        return current == owner ? read.Headers.ETag : null;
    }

    private static CosmosException Unexpected(ResponseMessage response) =>
        new(response.ErrorMessage ?? $"The lease write failed with status {response.StatusCode}.", response.StatusCode, 0,
            response.Headers.ActivityId, response.Headers.RequestCharge);
}
