using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using eQuantic.Core.Data.Diagnostics;
using Microsoft.Extensions.Logging;

namespace eQuantic.Core.Data.Relational.Diagnostics;

/// <summary>
///     The relational logging/metrics seam: a delegating <see cref="DbCommand" /> wrapped around every command
///     the engine creates, so <b>all</b> queries — reads, aggregates, set-based writes, includes — log through
///     one place with one shape: <c>CommandExecuted</c> (statement, elapsed) at Information,
///     <c>CommandFailed</c> at Error, both under <c>eQuantic.Core.Data.{provider}.Command</c>. Statements carry
///     placeholders; parameter values only appear behind
///     <c>DataConventions.EnableSensitiveDataLogging</c>. Metrics count and time every execution regardless of
///     log level.
/// </summary>
internal sealed class LoggingDbCommand(DbCommand inner, ILogger logger, string system, bool sensitive) : DbCommand
{
    [AllowNull]
    public override string CommandText { get => inner.CommandText; set => inner.CommandText = value!; }
    public override int CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }
    public override CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }
    public override bool DesignTimeVisible { get => inner.DesignTimeVisible; set => inner.DesignTimeVisible = value; }
    public override UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }
    protected override DbConnection? DbConnection { get => inner.Connection; set => inner.Connection = value; }
    protected override DbParameterCollection DbParameterCollection => inner.Parameters;
    protected override DbTransaction? DbTransaction { get => inner.Transaction; set => inner.Transaction = value; }

    public override void Cancel() => inner.Cancel();
    protected override DbParameter CreateDbParameter() => inner.CreateParameter();
    public override void Prepare() => inner.Prepare();

    public override int ExecuteNonQuery() => Execute(() => inner.ExecuteNonQuery(), static rows => rows);

    public override object? ExecuteScalar() => Execute(() => inner.ExecuteScalar(), static _ => (int?)null);

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        Execute(() => inner.ExecuteReader(behavior), static _ => (int?)null);

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(() => inner.ExecuteNonQueryAsync(cancellationToken), static rows => rows);

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(() => inner.ExecuteScalarAsync(cancellationToken), static _ => (int?)null);

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) =>
        ExecuteAsync(() => inner.ExecuteReaderAsync(behavior, cancellationToken), static _ => (int?)null);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => inner.DisposeAsync();

    private TResult Execute<TResult>(Func<TResult> run, Func<TResult, int?> rows)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = run();
            Succeeded(stopwatch.Elapsed.TotalMilliseconds, rows(result));
            return result;
        }
        catch (Exception exception)
        {
            Failed(stopwatch.Elapsed.TotalMilliseconds, exception);
            throw;
        }
    }

    private async Task<TResult> ExecuteAsync<TResult>(Func<Task<TResult>> run, Func<TResult, int?> rows)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await run().ConfigureAwait(false);
            Succeeded(stopwatch.Elapsed.TotalMilliseconds, rows(result));
            return result;
        }
        catch (Exception exception)
        {
            Failed(stopwatch.Elapsed.TotalMilliseconds, exception);
            throw;
        }
    }

    private void Succeeded(double elapsed, int? rows)
    {
        DataMetrics.Commands.Add(1, new KeyValuePair<string, object?>("db.system", system));
        DataMetrics.CommandDuration.Record(elapsed, new KeyValuePair<string, object?>("db.system", system));

        if (!logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        if (rows is { } affected)
        {
            logger.Log(LogLevel.Information, DataEvents.CommandExecuted,
                "Executed ({Elapsed:0.0} ms, {Rows} rows){Parameters}\n{Statement}",
                elapsed, affected, ParameterText(), inner.CommandText);
        }
        else
        {
            logger.Log(LogLevel.Information, DataEvents.CommandExecuted,
                "Executed ({Elapsed:0.0} ms){Parameters}\n{Statement}",
                elapsed, ParameterText(), inner.CommandText);
        }
    }

    private void Failed(double elapsed, Exception exception)
    {
        DataMetrics.CommandFailures.Add(1, new KeyValuePair<string, object?>("db.system", system));

        if (logger.IsEnabled(LogLevel.Error))
        {
            logger.Log(LogLevel.Error, DataEvents.CommandFailed, exception,
                "Failed ({Elapsed:0.0} ms){Parameters}\n{Statement}",
                elapsed, ParameterText(), inner.CommandText);
        }
    }

    /// <summary>Parameter values, only behind the explicit sensitive-data opt-in; empty otherwise.</summary>
    private string ParameterText()
    {
        if (!sensitive || inner.Parameters.Count == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder(" [");
        for (var index = 0; index < inner.Parameters.Count; index++)
        {
            if (index > 0)
            {
                text.Append(", ");
            }

            var parameter = inner.Parameters[index];
            text.Append(parameter.ParameterName).Append('=').Append(parameter.Value);
        }

        return text.Append(']').ToString();
    }
}
