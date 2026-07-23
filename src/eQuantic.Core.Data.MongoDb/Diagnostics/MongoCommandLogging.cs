using eQuantic.Core.Data.Diagnostics;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver.Core.Configuration;
using MongoDB.Driver.Core.Events;

namespace eQuantic.Core.Data.MongoDb.Diagnostics;

/// <summary>
///     The MongoDB logging/metrics seam: the driver's own command events, subscribed on the cluster at client
///     construction — every server command the provider issues (finds, aggregates, bulk writes) logs under
///     <c>eQuantic.Core.Data.mongodb.Command</c> with the driver-measured duration. Command <b>bodies</b> carry
///     values, so they only log behind <c>DataConventions.EnableSensitiveDataLogging</c>; without it the events
///     carry the command name and collection-agnostic facts only. Administrative chatter (handshakes, ping,
///     auth) is filtered out.
/// </summary>
internal static class MongoCommandLogging
{
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "hello", "isMaster", "ping", "buildInfo", "getParameter", "saslStart", "saslContinue",
        "endSessions", "getLog", "connectionStatus",
    };

    /// <summary>Subscribes the logging/metrics handlers on the cluster.</summary>
    /// <param name="cluster">The cluster builder.</param>
    /// <param name="logger">The command logger, or <c>null</c> for metrics only.</param>
    /// <param name="sensitive">Whether command bodies (which carry values) may log.</param>
    public static void Subscribe(ClusterBuilder cluster, ILogger? logger, bool sensitive)
    {
        cluster.Subscribe<CommandStartedEvent>(started =>
        {
            if (sensitive && logger is not null && !Noise.Contains(started.CommandName)
                && logger.IsEnabled(LogLevel.Information))
            {
                logger.Log(LogLevel.Information, DataEvents.CommandExecuted,
                    "Starting {Command}\n{Body}", started.CommandName, started.Command.ToJson());
            }
        });

        cluster.Subscribe<CommandSucceededEvent>(succeeded =>
        {
            if (Noise.Contains(succeeded.CommandName))
            {
                return;
            }

            DataMetrics.Commands.Add(1, new KeyValuePair<string, object?>("db.system", "mongodb"));
            DataMetrics.CommandDuration.Record(succeeded.Duration.TotalMilliseconds,
                new KeyValuePair<string, object?>("db.system", "mongodb"));

            if (logger is not null && logger.IsEnabled(LogLevel.Information))
            {
                logger.Log(LogLevel.Information, DataEvents.CommandExecuted,
                    "Executed {Command} ({Elapsed:0.0} ms)", succeeded.CommandName, succeeded.Duration.TotalMilliseconds);
            }
        });

        cluster.Subscribe<CommandFailedEvent>(failed =>
        {
            if (Noise.Contains(failed.CommandName))
            {
                return;
            }

            DataMetrics.CommandFailures.Add(1, new KeyValuePair<string, object?>("db.system", "mongodb"));
            logger?.Log(LogLevel.Error, DataEvents.CommandFailed, failed.Failure,
                "Failed {Command} ({Elapsed:0.0} ms)", failed.CommandName, failed.Duration.TotalMilliseconds);
        });
    }
}
