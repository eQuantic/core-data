using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using eQuantic.Core.Data.Diagnostics;
using global::Cassandra;

namespace eQuantic.Core.Data.Cassandra;

/// <summary>
///     A per-session prepared-statement cache: every repeated CQL text is prepared once against the cluster and
///     bound thereafter — the server parses the statement once, and the driver routes bound statements token-aware.
///     The cache lives and dies with its <see cref="ISession" />; a faulted preparation is evicted so the next call
///     retries instead of replaying the failure. DDL and other one-shot statements should keep using
///     <see cref="SimpleStatement" /> — preparing them buys nothing. Executions emit a <c>cassandra.execute</c>
///     activity on <see cref="DataActivitySource" /> carrying the statement text (placeholders, never values), the
///     consistency it was given and, for a lightweight transaction, the serial consistency it was pinned to.
/// </summary>
internal static class CassandraStatements
{
    private static readonly ConditionalWeakTable<ISession, ConcurrentDictionary<string, Task<PreparedStatement>>> Caches = new();

    /// <summary>The values one statement can bind: the native protocol counts them in an unsigned short.</summary>
    public const int MaxValues = ushort.MaxValue;

    /// <summary>Prepares (once) and binds the statement.</summary>
    public static async Task<BoundStatement> BindAsync(ISession session, string cql, object?[] values)
    {
        if (values.Length > MaxValues)
        {
            throw new NotSupportedException(
                $"A Cassandra statement binds at most {MaxValues:N0} values, and this one binds {values.Length:N0}; split the IN list it carries.");
        }

        var cache = Caches.GetOrCreateValue(session);
        var preparing = cache.GetOrAdd(cql, session.PrepareAsync);
        try
        {
            var prepared = await preparing.ConfigureAwait(false);
            return prepared.Bind(values);
        }
        catch
        {
            cache.TryRemove(new KeyValuePair<string, Task<PreparedStatement>>(cql, preparing));
            throw;
        }
    }

    /// <summary>Prepares (once), binds and executes the statement, optionally at an explicit consistency level.</summary>
    public static Task<RowSet> ExecuteAsync(ISession session, string cql, object?[] values,
        ConsistencyLevel? consistency = null, Microsoft.Extensions.Logging.ILogger? logger = null, bool sensitive = false) =>
        ExecuteCoreAsync(session, cql, values, consistency, null, logger, sensitive);

    /// <summary>
    ///     Executes a lightweight transaction (<c>IF</c>, <c>IF EXISTS</c>, <c>IF NOT EXISTS</c>), its Paxos rounds pinned to the
    ///     session's serial consistency (<see cref="CassandraLightweightTransactions.SerialConsistency" />) and its commit at
    ///     <paramref name="consistency" /> (the session's when <c>null</c>).
    /// </summary>
    public static Task<RowSet> ExecuteConditionalAsync(ISession session, string cql, object?[] values,
        ConsistencyLevel? consistency = null, Microsoft.Extensions.Logging.ILogger? logger = null, bool sensitive = false) =>
        ExecuteCoreAsync(session, cql, values, consistency, CassandraLightweightTransactions.SerialConsistency(session), logger, sensitive);

    /// <summary>
    ///     Executes a read at the session's serial consistency: it takes part in Paxos, so a transaction left in progress on
    ///     the partition is completed (or abandoned for good) first, and what it reads is what the transactions decided.
    /// </summary>
    public static Task<RowSet> ExecuteSerialAsync(ISession session, string cql, object?[] values,
        Microsoft.Extensions.Logging.ILogger? logger = null, bool sensitive = false) =>
        ExecuteCoreAsync(session, cql, values, CassandraLightweightTransactions.SerialConsistency(session), null, logger, sensitive);

    private static async Task<RowSet> ExecuteCoreAsync(ISession session, string cql, object?[] values,
        ConsistencyLevel? consistency, ConsistencyLevel? serialConsistency, Microsoft.Extensions.Logging.ILogger? logger, bool sensitive)
    {
        var bound = await BindAsync(session, cql, values).ConfigureAwait(false);
        if (consistency is { } level)
        {
            bound.SetConsistencyLevel(level);
        }

        if (serialConsistency is { } serial)
        {
            bound.SetSerialConsistencyLevel(serial);
        }

        using var activity = DataActivitySource.Instance.StartActivity("cassandra.execute", ActivityKind.Client);
        if (activity is not null)
        {
            activity.SetTag("db.system", "cassandra");
            activity.SetTag("db.statement", cql);
            if (consistency is { } tagged)
            {
                activity.SetTag("db.cassandra.consistency_level", tagged.ToString());
            }

            if (serialConsistency is { } taggedSerial)
            {
                activity.SetTag("db.cassandra.serial_consistency_level", taggedSerial.ToString());
            }
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var rows = await session.ExecuteAsync(bound).ConfigureAwait(false);

            DataMetrics.Commands.Add(1, new KeyValuePair<string, object?>("db.system", "cassandra"));
            DataMetrics.CommandDuration.Record(stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("db.system", "cassandra"));
            if (logger is not null && logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Information))
            {
                Microsoft.Extensions.Logging.LoggerExtensions.Log(logger,
                    Microsoft.Extensions.Logging.LogLevel.Information, DataEvents.CommandExecuted,
                    "Executed ({Elapsed:0.0} ms){Parameters}\n{Statement}",
                    stopwatch.Elapsed.TotalMilliseconds, sensitive && values.Length > 0 ? $" [{string.Join(", ", values)}]" : string.Empty, cql);
            }

            return rows;
        }
        catch (Exception exception)
        {
            DataMetrics.CommandFailures.Add(1, new KeyValuePair<string, object?>("db.system", "cassandra"));
            if (logger is not null)
            {
                Microsoft.Extensions.Logging.LoggerExtensions.Log(logger,
                    Microsoft.Extensions.Logging.LogLevel.Error, DataEvents.CommandFailed, exception,
                    "Failed ({Elapsed:0.0} ms)\n{Statement}", stopwatch.Elapsed.TotalMilliseconds, cql);
            }

            throw;
        }
    }
}
