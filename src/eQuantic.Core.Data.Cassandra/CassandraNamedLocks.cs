using eQuantic.Core.Data.Repository.Locking;
using global::Cassandra;

namespace eQuantic.Core.Data.Cassandra;

/// <summary>
///     Named locks on Cassandra, held as leases: one row per held name in the <see cref="TableName" /> table (created on
///     first use, in the session's keyspace), <c>(name PRIMARY KEY, owner)</c>, written with a time to live. Every write
///     is a lightweight transaction (Paxos, at the session's serial consistency — see
///     <see cref="CassandraLightweightTransactions" />), so of two owners racing for a name exactly one is applied:
///     the lease is taken with <c>INSERT … IF NOT EXISTS USING TTL</c>, renewed with
///     <c>UPDATE … USING TTL … IF owner = ?</c>, and released with <c>DELETE … IF owner = ?</c>. The expiry is the
///     row's TTL, counted by the cluster: once it runs out the row is gone and the next <c>IF NOT EXISTS</c> applies.
///     Lease writes never join a <c>LOGGED BATCH</c>: another holder has to see them while it is pending.
///     <para>
///         A TTL counts whole seconds from the <b>start</b> of the second the write lands in: Cassandra sets the
///         expiry to the write's time in seconds, truncated, plus the TTL, and the row is live while the time in
///         seconds is below it. A row written late in a second therefore expires almost a second before its TTL, and
///         a lease written with its own duration as the TTL could be taken over before that duration ran out. The TTL
///         is the duration rounded up to whole seconds plus that second (<see cref="Ttl" />), so a lease never expires
///         before its duration, and at most a second and a fraction after it.
///     </para>
/// </summary>
public static class CassandraNamedLocks
{
    /// <summary>The table holding the leases, one row per held name.</summary>
    public const string TableName = "equantic_locks";

    /// <summary>The leases of a session's keyspace.</summary>
    internal static LeaseStore Store(ISession session, Microsoft.Extensions.Logging.ILogger? logger, bool sensitive) =>
        new CassandraLeaseStore(session, logger, sensitive);

    /// <summary>
    ///     A lease's duration as a TTL: rounded up to whole seconds (at least one), plus the second Cassandra may cut
    ///     from it by counting the TTL from the start of the write's second.
    /// </summary>
    /// <param name="duration">The lease's duration.</param>
    internal static int Ttl(TimeSpan duration) => (int)Math.Max(1, Math.Ceiling(duration.TotalSeconds)) + 1;

    /// <summary>Creates the leases table once per session (a failed creation is tried again next time).</summary>
    internal static Task EnsureTableAsync(ISession session) =>
        CassandraSystemTables.EnsureAsync(session, TableName, "name text PRIMARY KEY, owner text");
}

/// <summary>The leases in a keyspace's <see cref="CassandraNamedLocks.TableName" /> table.</summary>
internal sealed class CassandraLeaseStore(ISession session, Microsoft.Extensions.Logging.ILogger? logger, bool sensitive)
    : LeaseStore
{
    public override async Task<Lease?> TryTakeAsync(string name, string owner, TimeSpan duration)
    {
        await CassandraNamedLocks.EnsureTableAsync(session).ConfigureAwait(false);
        var answer = (await CassandraStatements.ExecuteConditionalAsync(session,
            $"INSERT INTO {CassandraNamedLocks.TableName} (name, owner) VALUES (?, ?) IF NOT EXISTS USING TTL ?",
            [name, owner, CassandraNamedLocks.Ttl(duration)], null, logger, sensitive).ConfigureAwait(false)).First();
        return answer.GetValue<bool>("[applied]") ? new CassandraLease(session, name, owner, logger, sensitive) : null;
    }
}

/// <summary>A lease this process holds in the leases table.</summary>
internal sealed class CassandraLease(ISession session, string name, string owner,
    Microsoft.Extensions.Logging.ILogger? logger, bool sensitive) : Lease
{
    public override async Task<bool> RenewAsync(TimeSpan duration)
    {
        var answer = (await CassandraStatements.ExecuteConditionalAsync(session,
            $"UPDATE {CassandraNamedLocks.TableName} USING TTL ? SET owner = ? WHERE name = ? IF owner = ?",
            [CassandraNamedLocks.Ttl(duration), owner, name, owner], null, logger, sensitive).ConfigureAwait(false)).First();
        return answer.GetValue<bool>("[applied]");
    }

    public override Task ReleaseAsync() => CassandraStatements.ExecuteConditionalAsync(session,
        $"DELETE FROM {CassandraNamedLocks.TableName} WHERE name = ? IF owner = ?", [name, owner], null, logger, sensitive);
}
