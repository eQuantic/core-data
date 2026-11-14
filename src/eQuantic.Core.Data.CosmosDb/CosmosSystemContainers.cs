using System.Collections.Concurrent;
using System.Net;
using Microsoft.Azure.Cosmos;

namespace eQuantic.Core.Data.CosmosDb;

/// <summary>
///     The containers the provider keeps for itself in a database: the counters of
///     <see cref="CosmosSequences.ContainerName" /> and the leases of <see cref="CosmosNamedLocks.ContainerName" />, each
///     partitioned by <c>/id</c> and created on first use. The creation is remembered once per database and process
///     (a failed one is not), and forgotten again when the container answers that it is gone: a container removed
///     after its first use is created again by the next use that finds it missing.
///     <para>
///         Both hold items that a conditional write decides — a counter's <c>Patch</c> <c>Increment</c>, a lease's
///         create and ETag-guarded replace — and that decision is only one when one region decides it. On an account
///         that accepts writes in several regions each region applies the write to its own copy before the regions
///         converge, so two writers in two regions can both succeed: such an account is refused before the container
///         is first used (see <see cref="RefusalFor" />).
///     </para>
/// </summary>
internal static class CosmosSystemContainers
{
    /// <summary>The sub-status Cosmos DB answers a 404 with when the container (or its database) is not there.</summary>
    private const string OwnerResourceNotFound = "1003";

    private static readonly ConcurrentDictionary<string, Lazy<Task>> Ensured = new();
    private static readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<string>>>> WriteRegions = new();

    /// <summary>
    ///     The container, created on first use, once the account is known to take writes in a single region.
    /// </summary>
    /// <param name="database">The database holding it.</param>
    /// <param name="name">The container's name.</param>
    /// <param name="purpose">What it holds, for the refusal of an account with several write regions.</param>
    /// <param name="cancellationToken">A cancellation token (the creation itself, shared, is never cut off).</param>
    public static async Task<Container> GetAsync(Database database, string name, string purpose, CancellationToken cancellationToken)
    {
        var key = Key(database, name);
        var ensure = Ensured.GetOrAdd(key, _ => Creation(key, database, name, purpose));
        await ensure.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        return database.GetContainer(name);
    }

    /// <summary>
    ///     The shared creation, which forgets itself when it fails: the next use tries again, even when every caller that
    ///     waited for it had stopped waiting before it failed.
    /// </summary>
    private static Lazy<Task> Creation(string key, Database database, string name, string purpose)
    {
        Lazy<Task>? creation = null;
        creation = new Lazy<Task>(async () =>
        {
            try
            {
                await EnsureAsync(database, name, purpose).ConfigureAwait(false);
            }
            catch
            {
                Ensured.TryRemove(new KeyValuePair<string, Lazy<Task>>(key, creation!));
                throw;
            }
        });
        return creation;
    }

    /// <summary>
    ///     Forgets that the container was created and creates it again: the call to make when it answered that it is gone
    ///     (<see cref="IsGone(ResponseMessage)" />).
    /// </summary>
    /// <param name="database">The database holding it.</param>
    /// <param name="name">The container's name.</param>
    /// <param name="purpose">What it holds, for the refusal of an account with several write regions.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public static Task<Container> RecreateAsync(Database database, string name, string purpose, CancellationToken cancellationToken)
    {
        Ensured.TryRemove(Key(database, name), out _);
        return GetAsync(database, name, purpose, cancellationToken);
    }

    /// <summary>Whether a response says the container (or its database) is not there, rather than an item of it.</summary>
    /// <param name="response">The response of an operation on the container.</param>
    public static bool IsGone(ResponseMessage response) =>
        response.StatusCode == HttpStatusCode.NotFound && response.Headers["x-ms-substatus"] == OwnerResourceNotFound;

    /// <summary>Whether an exception says the container (or its database) is not there, rather than an item of it.</summary>
    /// <param name="exception">The exception of an operation on the container.</param>
    public static bool IsGone(CosmosException exception) =>
        exception.StatusCode == HttpStatusCode.NotFound && exception.SubStatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture) == OwnerResourceNotFound;

    /// <summary>
    ///     Why an account that takes writes in the given regions cannot hold <paramref name="purpose" />, or <c>null</c>
    ///     when it takes them in one. An account with a single write region lists only that one among its writable
    ///     regions, however many regions it reads from.
    /// </summary>
    /// <param name="writableRegions">The names of the account's writable regions.</param>
    /// <param name="purpose">What the provider was about to keep in the account, and what goes wrong with several.</param>
    public static string? RefusalFor(IReadOnlyCollection<string> writableRegions, string purpose) =>
        writableRegions.Count <= 1
            ? null
            : $"The Cosmos DB account accepts writes in {writableRegions.Count} regions ({string.Join(", ", writableRegions)}). " +
              $"{purpose} Use an account with a single write region for this database.";

    private static string Key(Database database, string name) => $"{database.Client.Endpoint}|{database.Id}|{name}";

    private static async Task EnsureAsync(Database database, string name, string purpose)
    {
        if (RefusalFor(await WritableRegionsAsync(database.Client).ConfigureAwait(false), purpose) is { } refusal)
        {
            throw new NotSupportedException(refusal);
        }

        await database.CreateContainerIfNotExistsAsync(new ContainerProperties(name, "/id"), cancellationToken: CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>The account's writable regions, read once per account and process (a failed read is not remembered).</summary>
    private static async Task<IReadOnlyList<string>> WritableRegionsAsync(CosmosClient client)
    {
        var key = client.Endpoint.ToString();
        var read = WriteRegions.GetOrAdd(key, _ => new Lazy<Task<IReadOnlyList<string>>>(async () =>
        {
            var account = await client.ReadAccountAsync().ConfigureAwait(false);
            return account.WritableRegions.Select(region => region.Name).ToList();
        }));
        try
        {
            return await read.Value.ConfigureAwait(false);
        }
        catch
        {
            WriteRegions.TryRemove(new KeyValuePair<string, Lazy<Task<IReadOnlyList<string>>>>(key, read));
            throw;
        }
    }
}
