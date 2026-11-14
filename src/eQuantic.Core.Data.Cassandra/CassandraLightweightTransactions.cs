using global::Cassandra;

namespace eQuantic.Core.Data.Cassandra;

/// <summary>
///     The rules every lightweight transaction (Paxos compare-and-set: <c>IF</c>, <c>IF EXISTS</c>, <c>IF NOT EXISTS</c>)
///     this provider runs follows, and every read that settles one.
///     <list type="bullet">
///         <item>
///             <b>One serial consistency.</b> A transaction's Paxos rounds run at a serial consistency, and the read that
///             settles what it decided has to run at the same one: a read at <c>LOCAL_SERIAL</c> does not see a round a
///             <c>SERIAL</c> transaction left in progress in another data center. The serial consistency is the session's
///             (<see cref="QueryOptions.GetSerialConsistencyLevel" />): <c>SERIAL</c>, the driver's default, unless the
///             cluster was built with <c>new QueryOptions().SetSerialConsistencyLevel(ConsistencyLevel.LocalSerial)</c>.
///             Every transaction is pinned to it and every serial read reads at it, so the two never disagree.
///             <c>LOCAL_SERIAL</c> runs Paxos among the local data center's replicas only: two data centers can each win a
///             compare-and-set of the same row, so a sequence could hand a number out twice and a named lock be held twice.
///             It suits a keyspace whose every writer of a row reaches it through one data center.
///         </item>
///         <item>
///             <b>One transaction at a time per partition.</b> Paxos runs per partition: transactions proposed at once on one
///             partition contend with each other, and past the contention timeout they fail with their outcome unknown.
///             A write that runs a transaction per row runs a partition's one after the other, and several partitions
///             side by side (<see cref="PerPartitionAsync{TItem,TResult}" />).
///         </item>
///     </list>
/// </summary>
internal static class CassandraLightweightTransactions
{
    /// <summary>How many partitions a write runs its transactions in at once.</summary>
    private const int Partitions = 16;

    /// <summary>The serial consistency every transaction is pinned to and every serial read reads at: the session's.</summary>
    /// <param name="session">The session.</param>
    public static ConsistencyLevel SerialConsistency(ISession session) =>
        session.Cluster.Configuration.QueryOptions.GetSerialConsistencyLevel() switch
        {
            ConsistencyLevel.LocalSerial => ConsistencyLevel.LocalSerial,
            ConsistencyLevel.Serial => ConsistencyLevel.Serial,
            var other => throw new NotSupportedException(
                $"The session's serial consistency is {other}; a lightweight transaction runs at SERIAL or LOCAL_SERIAL."),
        };

    /// <summary>
    ///     Runs <paramref name="run" /> for every item, one item at a time within a partition and up to
    ///     <see cref="Partitions" /> partitions at once, and answers the results in the items' order.
    /// </summary>
    /// <param name="items">The items, each written by a transaction of its own.</param>
    /// <param name="partition">The partition an item writes, as text.</param>
    /// <param name="run">The transaction of an item.</param>
    public static async Task<TResult[]> PerPartitionAsync<TItem, TResult>(IReadOnlyList<TItem> items, Func<TItem, string> partition,
        Func<TItem, Task<TResult>> run)
    {
        var results = new TResult[items.Count];
        using var slots = new SemaphoreSlim(Partitions);
        await Task.WhenAll(Enumerable.Range(0, items.Count).GroupBy(index => partition(items[index]), StringComparer.Ordinal)
            .Select(async group =>
            {
                await slots.WaitAsync().ConfigureAwait(false);
                try
                {
                    foreach (var index in group)
                    {
                        results[index] = await run(items[index]).ConfigureAwait(false);
                    }
                }
                finally
                {
                    slots.Release();
                }
            })).ConfigureAwait(false);
        return results;
    }

    /// <summary>A jittered pause after a lost race, so contending transactions stop proposing in lockstep.</summary>
    /// <param name="attempt">The attempt that lost, from 1.</param>
    public static Task BackoffAsync(int attempt) => Task.Delay(Random.Shared.Next(1, 10 * Math.Min(attempt, 10) + 2));

    /// <summary>Whether an exception is a lightweight transaction that timed out with its outcome unknown.</summary>
    /// <param name="exception">The exception.</param>
    public static bool IsTimedOut(Exception exception) => exception is WriteTimeoutException { WriteType: "CAS" };
}
