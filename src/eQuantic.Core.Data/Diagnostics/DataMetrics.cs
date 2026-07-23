using System.Diagnostics.Metrics;

namespace eQuantic.Core.Data.Diagnostics;

/// <summary>
///     The engine's metrics — one <see cref="Meter" /> (<c>eQuantic.Core.Data</c>) any OpenTelemetry setup
///     subscribes with <c>AddMeter("eQuantic.Core.Data")</c>. Counters carry a <c>db.system</c> tag; the gate
///     counters make the engine's honesty <b>graphable</b>: a rising <c>client_evaluations</c> in a hot path is
///     an alert, not an archaeology project.
/// </summary>
public static class DataMetrics
{
    /// <summary>The meter name to subscribe (<c>AddMeter("eQuantic.Core.Data")</c>).</summary>
    public const string MeterName = "eQuantic.Core.Data";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Commands executed, tagged <c>db.system</c>.</summary>
    public static readonly Counter<long> Commands = Meter.CreateCounter<long>(
        "equantic.commands", description: "Commands executed");

    /// <summary>Command duration in milliseconds, tagged <c>db.system</c>.</summary>
    public static readonly Histogram<double> CommandDuration = Meter.CreateHistogram<double>(
        "equantic.command.duration", unit: "ms", description: "Command duration");

    /// <summary>Command failures, tagged <c>db.system</c>.</summary>
    public static readonly Counter<long> CommandFailures = Meter.CreateCounter<long>(
        "equantic.command.failures", description: "Commands that threw");

    /// <summary>Commits flushed, tagged <c>db.system</c>.</summary>
    public static readonly Counter<long> Commits = Meter.CreateCounter<long>(
        "equantic.commits", description: "Commits flushed");

    /// <summary>Staged writes flushed by commits, tagged <c>db.system</c>.</summary>
    public static readonly Counter<long> Writes = Meter.CreateCounter<long>(
        "equantic.writes", description: "Staged writes flushed");

    /// <summary>Queries whose residual ran client-side (behind the opt-in), tagged <c>db.system</c>.</summary>
    public static readonly Counter<long> ClientEvaluations = Meter.CreateCounter<long>(
        "equantic.client_evaluations", description: "Queries with a client-side residual");

    /// <summary>Queries that ran as declared scans (Cassandra ALLOW FILTERING), tagged <c>db.system</c>.</summary>
    public static readonly Counter<long> AllowFiltering = Meter.CreateCounter<long>(
        "equantic.allow_filtering", description: "Queries running as declared scans");

    /// <summary>OR filters split into parallel native queries, tagged <c>db.system</c>.</summary>
    public static readonly Counter<long> QuerySplits = Meter.CreateCounter<long>(
        "equantic.query_splits", description: "OR-split query executions");

    /// <summary>Optimistic-concurrency commits that lost their race, tagged <c>db.system</c>.</summary>
    public static readonly Counter<long> ConcurrencyConflicts = Meter.CreateCounter<long>(
        "equantic.concurrency_conflicts", description: "Commits that hit a concurrency conflict");
}
