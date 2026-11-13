using System.Runtime.CompilerServices;
using global::Cassandra;

namespace eQuantic.Core.Data.Cassandra;

/// <summary>
///     The sequences behind store-generated integral columns. A Cassandra <c>counter</c> cannot be read atomically after
///     its increment, so two writers could read the same value; a sequence is instead one row per table and column in
///     the <see cref="TableName" /> table, advanced by a lightweight-transaction compare-and-set
///     (<c>UPDATE … SET value = ? … IF value = ?</c>, Paxos) that only one writer can win for a given value — a number is
///     never handed out twice. A commit reserves every number it needs from a sequence in one compare-and-set. The
///     table is created on first use, in the session's keyspace.
/// </summary>
public static class CassandraSequences
{
    /// <summary>The table holding the sequences, one row per <c>&lt;table&gt;.&lt;column&gt;</c>.</summary>
    public const string TableName = "equantic_sequences";

    private const int Attempts = 64;
    private static readonly ConditionalWeakTable<ISession, Lazy<Task>> Ensured = new();

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
        await EnsureTableAsync(session).ConfigureAwait(false);

        // The current value, read through Paxos (SERIAL) so it is the last one a compare-and-set committed.
        var read = await CassandraStatements.BindAsync(session, $"SELECT value FROM {TableName} WHERE name = ?", [sequence])
            .ConfigureAwait(false);
        read.SetConsistencyLevel(ConsistencyLevel.Serial);
        var current = (await session.ExecuteAsync(read).ConfigureAwait(false)).FirstOrDefault() is { } row && !row.IsNull("value")
            ? row.GetValue<long>("value")
            : (long?)null;

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var (cql, values) = current is { } value
                ? ($"UPDATE {TableName} SET value = ? WHERE name = ? IF value = ?", new object?[] { value + count, sequence, value })
                : ($"INSERT INTO {TableName} (name, value) VALUES (?, ?) IF NOT EXISTS", new object?[] { sequence, count });

            var answer = (await CassandraStatements.ExecuteAsync(session, cql, values, null, logger, sensitive).ConfigureAwait(false)).First();
            if (answer.GetValue<bool>("[applied]"))
            {
                return (current ?? 0L) + count;
            }

            // Another writer moved the sequence first: the unapplied answer carries the value it holds now.
            current = answer.IsNull("value") ? null : answer.GetValue<long>("value");
        }

        throw new InvalidOperationException(
            $"The sequence '{sequence}' in '{TableName}' could not be advanced after {Attempts} contended attempts.");
    }

    /// <summary>Creates the sequences table once per session (a failed creation is tried again next time).</summary>
    private static async Task EnsureTableAsync(ISession session)
    {
        var ensure = Ensured.GetValue(session, key => new Lazy<Task>(() => key.ExecuteAsync(new SimpleStatement(
            $"CREATE TABLE IF NOT EXISTS {TableName} (name text PRIMARY KEY, value bigint)"))));
        try
        {
            await ensure.Value.ConfigureAwait(false);
        }
        catch
        {
            Ensured.Remove(session);
            throw;
        }
    }
}
