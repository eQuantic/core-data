using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using global::Cassandra;

namespace eQuantic.Core.Data.Cassandra;

/// <summary>
///     The tables the provider keeps for itself in the session's keyspace (the sequences, the leases of named locks),
///     each created on first use, once per session. A creation that fails is tried again by the next caller.
/// </summary>
internal static class CassandraSystemTables
{
    private static readonly ConditionalWeakTable<ISession, ConcurrentDictionary<string, Lazy<Task>>> Ensured = new();

    /// <summary>Creates the table if it does not exist, once per session.</summary>
    /// <param name="session">The session, bound to the keyspace.</param>
    /// <param name="table">The table's name.</param>
    /// <param name="columns">Its column definitions, primary key included (<c>name text PRIMARY KEY, value bigint</c>).</param>
    public static async Task EnsureAsync(ISession session, string table, string columns)
    {
        var tables = Ensured.GetOrCreateValue(session);
        var ensure = tables.GetOrAdd(table, _ => new Lazy<Task>(() => session.ExecuteAsync(new SimpleStatement(
            $"CREATE TABLE IF NOT EXISTS {table} ({columns})"))));
        try
        {
            await ensure.Value.ConfigureAwait(false);
        }
        catch
        {
            tables.TryRemove(new KeyValuePair<string, Lazy<Task>>(table, ensure));
            throw;
        }
    }
}
