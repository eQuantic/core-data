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
///     activity on <see cref="DataActivitySource" /> carrying the statement text (placeholders, never values).
/// </summary>
internal static class CassandraStatements
{
    private static readonly ConditionalWeakTable<ISession, ConcurrentDictionary<string, Task<PreparedStatement>>> Caches = new();

    /// <summary>Prepares (once) and binds the statement.</summary>
    public static async Task<BoundStatement> BindAsync(ISession session, string cql, object?[] values)
    {
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
    public static async Task<RowSet> ExecuteAsync(ISession session, string cql, object?[] values,
        ConsistencyLevel? consistency = null)
    {
        var bound = await BindAsync(session, cql, values).ConfigureAwait(false);
        if (consistency is { } level)
        {
            bound.SetConsistencyLevel(level);
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
        }

        return await session.ExecuteAsync(bound).ConfigureAwait(false);
    }
}
