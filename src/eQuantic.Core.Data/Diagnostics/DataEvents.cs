using Microsoft.Extensions.Logging;

namespace eQuantic.Core.Data.Diagnostics;

/// <summary>
///     The engine's log events — stable ids under stable categories, the way EF Core's are, so any
///     <c>Microsoft.Extensions.Logging</c> sink (Serilog, NLog, the console) filters them with the ritual it
///     already knows: <c>MinimumLevel.Override("eQuantic.Core.Data", …)</c>. Categories follow
///     <c>eQuantic.Core.Data.{Provider}.Command</c> / <c>.Commit</c> plus <c>eQuantic.Core.Data.Query</c> for
///     the pushdown gates. Statements log with placeholders; parameter values only appear behind
///     <see cref="Repository.DataConventions.EnableSensitiveDataLogging" />.
/// </summary>
public static class DataEvents
{
    /// <summary>A command completed (statement, elapsed, rows where known). Information.</summary>
    public static readonly EventId CommandExecuted = new(10001, nameof(CommandExecuted));

    /// <summary>A command failed (statement plus the exception). Error.</summary>
    public static readonly EventId CommandFailed = new(10002, nameof(CommandFailed));

    /// <summary>A commit flushed its staged writes (count, elapsed). Information.</summary>
    public static readonly EventId CommitExecuted = new(10101, nameof(CommitExecuted));

    /// <summary>A filter's residual ran client-side over the fetched rows (behind its opt-in). Warning.</summary>
    public static readonly EventId ClientEvaluation = new(10201, nameof(ClientEvaluation));

    /// <summary>A query ran as a server-side scan (Cassandra <c>ALLOW FILTERING</c>, behind its opt-in). Warning.</summary>
    public static readonly EventId AllowFiltering = new(10202, nameof(AllowFiltering));

    /// <summary>An OR filter split into parallel native queries merged client-side. Warning.</summary>
    public static readonly EventId QuerySplit = new(10203, nameof(QuerySplit));

    /// <summary>An optimistic-concurrency commit lost its race (nothing was applied). Warning.</summary>
    public static readonly EventId ConcurrencyConflict = new(10301, nameof(ConcurrencyConflict));
}
