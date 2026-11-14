using global::Cassandra;

namespace eQuantic.Core.Data.Cassandra;

/// <summary>
///     The sequences behind store-generated integral columns. A Cassandra <c>counter</c> cannot be read atomically after
///     its increment, so two writers could read the same value; a sequence is instead one row per table and column in
///     the <see cref="TableName" /> table, advanced by a lightweight-transaction compare-and-set
///     (<c>UPDATE … SET value = ? … IF value = ?</c>, Paxos) that only one writer can win for a given value — a number is
///     never handed out twice. A write reserves every number it needs from a sequence in one compare-and-set. The
///     table is created on first use, in the session's keyspace.
///     <para>
///         A compare-and-set that times out under contention has an outcome nobody can know until a serial read settles
///         it, and even then a value it wrote is indistinguishable from another writer's of the same sum. The sequence is
///         advanced again from the value the serial read finds: if the first one applied, its numbers are skipped — a gap,
///         as a write that fails after its reservation leaves, and never a number handed out twice.
///     </para>
/// </summary>
public static class CassandraSequences
{
    /// <summary>The table holding the sequences, one row per <c>&lt;table&gt;.&lt;column&gt;</c>.</summary>
    public const string TableName = "equantic_sequences";

    private const int Attempts = 64;

    /// <summary>The sequence's row name for a column of a table.</summary>
    /// <param name="table">The entity's table.</param>
    /// <param name="column">The stored column name.</param>
    public static string SequenceName(string table, string column) => $"{table}.{column}";

    /// <summary>
    ///     Advances a sequence by <paramref name="count" /> and returns its new value: the numbers
    ///     <c>value - count + 1</c> through <c>value</c> are the caller's, and nobody else's.
    /// </summary>
    internal static async Task<long> AdvanceAsync(ISession session, string sequence, long count,
        Microsoft.Extensions.Logging.ILogger? logger, bool sensitive)
    {
        await CassandraSystemTables.EnsureAsync(session, TableName, "name text PRIMARY KEY, value bigint").ConfigureAwait(false);

        var current = await ReadAsync(session, sequence, logger, sensitive).ConfigureAwait(false);
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            var (cql, values) = current is { } value
                ? ($"UPDATE {TableName} SET value = ? WHERE name = ? IF value = ?", new object?[] { value + count, sequence, value })
                : ($"INSERT INTO {TableName} (name, value) VALUES (?, ?) IF NOT EXISTS", new object?[] { sequence, count });

            Row answer;
            try
            {
                answer = (await CassandraStatements.ExecuteConditionalAsync(session, cql, values, null, logger, sensitive)
                    .ConfigureAwait(false)).First();
            }
            catch (Exception exception) when (CassandraLightweightTransactions.IsTimedOut(exception))
            {
                // Its outcome unknown: advance again from what the serial read settles. Numbers it may have taken are skipped.
                current = await ReadAsync(session, sequence, logger, sensitive).ConfigureAwait(false);
                await CassandraLightweightTransactions.BackoffAsync(attempt).ConfigureAwait(false);
                continue;
            }

            if (answer.GetValue<bool>("[applied]"))
            {
                return (current ?? 0L) + count;
            }

            // Another writer moved the sequence first: the unapplied answer carries the value it holds now.
            current = answer.IsNull("value") ? null : answer.GetValue<long>("value");
            await CassandraLightweightTransactions.BackoffAsync(attempt).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"The sequence '{sequence}' in '{TableName}' could not be advanced after {Attempts} contended attempts.");
    }

    /// <summary>
    ///     The sequence's value, read at the serial consistency so it is the last one a compare-and-set committed (a read
    ///     that times out under the same contention is read again).
    /// </summary>
    private static async Task<long?> ReadAsync(ISession session, string sequence, Microsoft.Extensions.Logging.ILogger? logger, bool sensitive)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var rows = await CassandraStatements.ExecuteSerialAsync(session, $"SELECT value FROM {TableName} WHERE name = ?", [sequence],
                    logger, sensitive).ConfigureAwait(false);
                return rows.FirstOrDefault() is { } row && !row.IsNull("value") ? row.GetValue<long>("value") : null;
            }
            catch (Exception exception) when (exception is ReadTimeoutException or WriteTimeoutException && attempt < Attempts)
            {
                await CassandraLightweightTransactions.BackoffAsync(attempt).ConfigureAwait(false);
            }
        }
    }
}
