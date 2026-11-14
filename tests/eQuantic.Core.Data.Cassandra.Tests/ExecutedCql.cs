using System.Collections.Concurrent;
using System.Diagnostics;
using eQuantic.Core.Data.Diagnostics;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>
///     Records every statement the provider executes while it is alive, from the <c>cassandra.execute</c> spans it emits on
///     <see cref="DataActivitySource" />: the CQL (placeholders, never values), the consistency the statement was given, and
///     the serial consistency a lightweight transaction was pinned to. A test filters by the tables it owns, since the
///     listener hears every span in the process.
/// </summary>
internal sealed class ExecutedCql : IDisposable
{
    private readonly ConcurrentQueue<ExecutedStatement> _executed = new();
    private readonly ActivityListener _listener;

    public ExecutedCql()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DataActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "cassandra.execute" && activity.GetTagItem("db.statement") is string cql)
                {
                    _executed.Enqueue(new ExecutedStatement(cql,
                        activity.GetTagItem("db.cassandra.consistency_level") as string,
                        activity.GetTagItem("db.cassandra.serial_consistency_level") as string));
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>The statements executed so far, in the order they finished.</summary>
    public IReadOnlyList<ExecutedStatement> Statements => _executed.ToArray();

    /// <summary>The statements executed so far that name the table.</summary>
    /// <param name="table">The table's name, matched as a whole word and ignoring case.</param>
    public IReadOnlyList<ExecutedStatement> On(string table) =>
        Statements.Where(statement => statement.Names(table)).ToList();

    /// <summary>Forgets what was recorded.</summary>
    public void Clear() => _executed.Clear();

    public void Dispose() => _listener.Dispose();
}

/// <summary>One statement the provider executed.</summary>
/// <param name="Cql">The statement's text.</param>
/// <param name="Consistency">The consistency it was given, when the provider set one.</param>
/// <param name="SerialConsistency">The serial consistency it was pinned to, when it is a lightweight transaction.</param>
internal sealed record ExecutedStatement(string Cql, string? Consistency, string? SerialConsistency)
{
    /// <summary>The values it binds.</summary>
    public int Markers => Cql.Count(character => character == '?');

    /// <summary>Whether it is a lightweight transaction (<c>IF</c>, <c>IF EXISTS</c>, <c>IF NOT EXISTS</c>).</summary>
    public bool IsConditional => System.Text.RegularExpressions.Regex.IsMatch(Cql, @"\sIF\s", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Whether it is a read.</summary>
    public bool IsRead => Cql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether it names the table.</summary>
    /// <param name="table">The table's name.</param>
    public bool Names(string table) =>
        System.Text.RegularExpressions.Regex.IsMatch(Cql, $@"\b{System.Text.RegularExpressions.Regex.Escape(table)}\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
