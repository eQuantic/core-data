using System.Diagnostics;
using System.Net;
using eQuantic.Core.Data.Diagnostics;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace eQuantic.Core.Data.CosmosDb.Diagnostics;

/// <summary>
///     The Cosmos DB logging/metrics seam: a <see cref="RequestHandler" /> in the client pipeline, so every
///     data-plane operation the provider issues logs under <c>eQuantic.Core.Data.cosmosdb.Request</c> with the
///     facts Cosmos operators actually chase — status, elapsed and the <b>request charge (RU)</b>. Request
///     bodies (queries carry parameter values) never log; a 404 on a point read is a normal miss and logs as an
///     executed request, not an error.
/// </summary>
internal sealed class CosmosRequestLogging(ILogger? logger) : RequestHandler
{
    /// <inheritdoc />
    public override async Task<ResponseMessage> SendAsync(RequestMessage request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            DataMetrics.Commands.Add(1, new KeyValuePair<string, object?>("db.system", "cosmosdb"));
            DataMetrics.CommandDuration.Record(stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("db.system", "cosmosdb"));

            if (logger is not null)
            {
                var failed = !response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound;
                if (failed)
                {
                    DataMetrics.CommandFailures.Add(1, new KeyValuePair<string, object?>("db.system", "cosmosdb"));
                }

                var level = failed ? LogLevel.Error : LogLevel.Information;
                if (logger.IsEnabled(level))
                {
                    logger.Log(level, failed ? DataEvents.CommandFailed : DataEvents.CommandExecuted,
                        "{Method} {Resource} -> {Status} ({Elapsed:0.0} ms, {Charge:0.##} RU)",
                        request.Method, request.RequestUri, (int)response.StatusCode,
                        stopwatch.Elapsed.TotalMilliseconds, response.Headers.RequestCharge);
                }
            }

            return response;
        }
        catch (CosmosException exception)
        {
            DataMetrics.CommandFailures.Add(1, new KeyValuePair<string, object?>("db.system", "cosmosdb"));
            logger?.Log(LogLevel.Error, DataEvents.CommandFailed, exception,
                "{Method} {Resource} failed ({Elapsed:0.0} ms)", request.Method, request.RequestUri,
                stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }
}
