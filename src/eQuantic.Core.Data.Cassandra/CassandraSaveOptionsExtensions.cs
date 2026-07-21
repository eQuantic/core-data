using System.Runtime.CompilerServices;
using eQuantic.Core.Data.Repository.Options;
using global::Cassandra;

namespace eQuantic.Core.Data.Cassandra;

/// <summary>
///     Cassandra-specific save opt-ins, carried on the <see cref="SaveOptions" /> a commit receives
///     (<c>uow.CommitAsync(o =&gt; o.WithTtl(...).WithConsistency(...))</c>) — the point where
///     <c>Commit(Action&lt;SaveOptions&gt;)</c> stops being a no-op for this provider.
/// </summary>
public static class CassandraSaveOptionsExtensions
{
    private static readonly ConditionalWeakTable<SaveOptions, object> Consistencies = new();
    private static readonly ConditionalWeakTable<SaveOptions, object> TimesToLive = new();

    /// <summary>Applies a consistency level to every write flushed by this commit.</summary>
    /// <param name="options">The save options.</param>
    /// <param name="consistency">The consistency level (e.g. <see cref="ConsistencyLevel.LocalQuorum" />).</param>
    /// <returns>The same options for chaining.</returns>
    public static SaveOptions WithConsistency(this SaveOptions options, ConsistencyLevel consistency)
    {
        Consistencies.AddOrUpdate(options, consistency);
        return options;
    }

    /// <summary>
    ///     Applies a time-to-live to every <b>insert</b> flushed by this commit (<c>USING TTL</c>): the rows
    ///     expire that long after the write. Deletes are unaffected.
    /// </summary>
    /// <param name="options">The save options.</param>
    /// <param name="timeToLive">The time-to-live.</param>
    /// <returns>The same options for chaining.</returns>
    public static SaveOptions WithTtl(this SaveOptions options, TimeSpan timeToLive)
    {
        TimesToLive.AddOrUpdate(options, (int)timeToLive.TotalSeconds);
        return options;
    }

    internal static ConsistencyLevel? ConsistencyOf(SaveOptions options) =>
        Consistencies.TryGetValue(options, out var value) ? (ConsistencyLevel)value : null;

    internal static int? TtlOf(SaveOptions options) =>
        TimesToLive.TryGetValue(options, out var value) ? (int)value : null;
}
