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
///         A write answered 408 or 503 may or may not have landed, so it is settled by reading the item back: one
///         that names this owner (that write, or an earlier one of the same acquisition) is replaced once more under
///         its ETag, so the lease's term counts from a write of the current call, and anything else is another
///         owner's or nobody's. A 409 on an item that names this owner is settled the same way.
///     </para>
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

    /// <summary>Whether a write answered so may have landed all the same (a timeout, or a service unavailable meanwhile).</summary>
    internal static bool AnswerLost(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout or HttpStatusCode.ServiceUnavailable;

    /// <summary>The exception a lease call answered with an unexpected status throws.</summary>
    internal static CosmosException Unexpected(ResponseMessage response) =>
        new(response.ErrorMessage ?? $"The lease write failed with status {response.StatusCode}.", response.StatusCode, 0,
            response.Headers.ActivityId, response.Headers.RequestCharge);

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
    /// <summary>How many times one call reads the item back after a write whose answer was lost, before it gives up.</summary>
    private const int Settlements = 3;

    public override async Task<Lease?> TryTakeAsync(string name, string owner, TimeSpan duration, CancellationToken cancellationToken)
    {
        var container = await CosmosNamedLocks.LeasesAsync(database).ConfigureAwait(false);
        var id = CosmosNamedLocks.ItemId(name);
        var partitionKey = new PartitionKey(id);

        using (var created = await container.CreateItemStreamAsync(CosmosNamedLocks.Body(id, name, owner, duration), partitionKey,
                   cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (created.IsSuccessStatusCode)
            {
                return new CosmosLease(container, id, name, owner, created.Headers.ETag);
            }

            if (created.StatusCode != HttpStatusCode.Conflict && !CosmosNamedLocks.AnswerLost(created.StatusCode))
            {
                throw CosmosNamedLocks.Unexpected(created);
            }
        }

        // An item is there (or the create landed although its answer was lost): the item says whose it is.
        for (var settlement = 0; settlement < Settlements; settlement++)
        {
            using var read = await container.ReadItemStreamAsync(id, partitionKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (read.StatusCode == HttpStatusCode.NotFound)
            {
                return null; // Released meanwhile, or the create never landed: the next attempt creates it.
            }

            if (!read.IsSuccessStatusCode)
            {
                throw CosmosNamedLocks.Unexpected(read);
            }

            var (current, writtenAt, leaseSeconds) = await CosmosNamedLocks.ReadAsync(read.Content).ConfigureAwait(false);
            if (current != owner)
            {
                // Another owner's lease: taken over only once it has expired by the server's clock. _ts counts whole
                // seconds, so the write happened before _ts + 1: the lease is surely over past that plus its length.
                var now = await CosmosSequences.ServerClockAsync(container, partitionKey, cancellationToken).ConfigureAwait(false);
                var expiresAt = DateTimeOffset.FromUnixTimeSeconds(writtenAt + 1).UtcDateTime.AddSeconds(leaseSeconds);
                if (now < expiresAt)
                {
                    return null;
                }
            }

            // Another owner's expired lease is taken over; this owner's own (a write of this acquisition whose answer
            // was lost) is written again, so its term counts from this call. The item's ETag guards both.
            using var replaced = await container.ReplaceItemStreamAsync(CosmosNamedLocks.Body(id, name, owner, duration), id,
                partitionKey, new ItemRequestOptions { IfMatchEtag = read.Headers.ETag }, cancellationToken).ConfigureAwait(false);
            if (replaced.IsSuccessStatusCode)
            {
                return new CosmosLease(container, id, name, owner, replaced.Headers.ETag);
            }

            if (replaced.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
            {
                return null; // Another owner took it over (or it was released) first; the next attempt sees which.
            }

            if (!CosmosNamedLocks.AnswerLost(replaced.StatusCode))
            {
                throw CosmosNamedLocks.Unexpected(replaced);
            }
        }

        throw new CosmosException($"The lease of '{name}' could not be settled: every write's answer was lost.",
            HttpStatusCode.RequestTimeout, 0, string.Empty, 0);
    }

    public override async Task ReleaseAsync(string name, string owner, CancellationToken cancellationToken)
    {
        var container = await CosmosNamedLocks.LeasesAsync(database).ConfigureAwait(false);
        var id = CosmosNamedLocks.ItemId(name);
        var lease = new CosmosLease(container, id, name, owner, etag: null);
        if (await lease.CurrentEtagIfMineAsync(cancellationToken).ConfigureAwait(false) is { } etag)
        {
            await new CosmosLease(container, id, name, owner, etag).ReleaseAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>A lease this process holds, addressed by the ETag of its last write.</summary>
internal sealed class CosmosLease(Container container, string id, string name, string owner, string? etag) : Lease
{
    private const int Attempts = 3;

    private readonly PartitionKey _partitionKey = new(id);
    private string? _etag = etag;

    public override async Task<bool> RenewAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            using var replaced = await container.ReplaceItemStreamAsync(CosmosNamedLocks.Body(id, name, owner, duration), id,
                _partitionKey, new ItemRequestOptions { IfMatchEtag = _etag }, cancellationToken).ConfigureAwait(false);
            if (replaced.IsSuccessStatusCode)
            {
                _etag = replaced.Headers.ETag;
                return true;
            }

            if (replaced.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            if (replaced.StatusCode != HttpStatusCode.PreconditionFailed && !CosmosNamedLocks.AnswerLost(replaced.StatusCode))
            {
                throw CosmosNamedLocks.Unexpected(replaced);
            }

            // The ETag moved (a renewal of this owner whose answer was lost, or another owner's takeover), or this
            // renewal's own answer was lost. Only the item says whose it is; this owner's is written once more, so the
            // new term counts from this call whichever write moved it.
            if (await CurrentEtagIfMineAsync(cancellationToken).ConfigureAwait(false) is not { } current)
            {
                return false;
            }

            _etag = current;
        }

        throw new CosmosException($"The renewal of the lease '{name}' could not be confirmed.", HttpStatusCode.RequestTimeout,
            0, string.Empty, 0);
    }

    public override async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            using var deleted = await container.DeleteItemStreamAsync(id, _partitionKey,
                new ItemRequestOptions { IfMatchEtag = _etag }, cancellationToken).ConfigureAwait(false);
            if (deleted.IsSuccessStatusCode || deleted.StatusCode == HttpStatusCode.NotFound)
            {
                return;
            }

            if (deleted.StatusCode != HttpStatusCode.PreconditionFailed && !CosmosNamedLocks.AnswerLost(deleted.StatusCode))
            {
                throw CosmosNamedLocks.Unexpected(deleted);
            }

            if (await CurrentEtagIfMineAsync(cancellationToken).ConfigureAwait(false) is not { } current)
            {
                return; // Another owner's now, or gone: left alone.
            }

            _etag = current;
        }
    }

    /// <summary>The item's current ETag when it is still this owner's, or <c>null</c>.</summary>
    internal async Task<string?> CurrentEtagIfMineAsync(CancellationToken cancellationToken)
    {
        using var read = await container.ReadItemStreamAsync(id, _partitionKey, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (read.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!read.IsSuccessStatusCode)
        {
            throw CosmosNamedLocks.Unexpected(read);
        }

        var (current, _, _) = await CosmosNamedLocks.ReadAsync(read.Content).ConfigureAwait(false);
        return current == owner ? read.Headers.ETag : null;
    }
}
