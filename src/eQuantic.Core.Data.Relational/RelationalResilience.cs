using System.Data.Common;
using System.Diagnostics;

namespace eQuantic.Core.Data.Relational;

/// <summary>
///     The transient-fault retry policy for the relational engine — <b>opt-in</b> via
///     <c>services.AddRelationalResilience(...)</c>. Retries follow honest semantics:
///     <list type="bullet">
///         <item><b>Reads</b> retry automatically: they are idempotent, and the broken pooled connection is
///         reset before the next attempt.</item>
///         <item><b>Commits</b> retry only behind <see cref="RetryCommits" />: a commit that failed after the
///         server applied it would re-run the whole batch. Concurrency tokens and client-generated keys make a
///         double-apply fail loudly instead of silently — weigh that before opting in.</item>
///         <item>Inside an <b>explicit transaction</b> nothing retries — the transaction is broken; the caller
///         must roll back and restart it.</item>
///     </list>
///     Transience comes from the driver itself (<see cref="DbException.IsTransient" />); attempts back off
///     exponentially with jitter and are tagged on the current span (<c>equantic.retries</c>). The document and
///     wide-column providers need none of this: their drivers ship native retry policies.
/// </summary>
public sealed class RelationalResilienceOptions
{
    /// <summary>The maximum number of retries after the first attempt (3 by default).</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>The base backoff delay; attempt <c>n</c> waits roughly <c>BaseDelay × 2ⁿ</c> plus jitter (200 ms by default).</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    ///     Whether commits retry too. Off by default: a commit whose acknowledgement was lost may have been
    ///     applied, and a retry re-runs the whole batch — enable it when concurrency tokens or client-generated
    ///     keys make a double-apply detectable.
    /// </summary>
    public bool RetryCommits { get; set; }
}

/// <summary>Runs an operation under the retry policy. The unit of work delegates here.</summary>
internal static class RelationalResilience
{
    /// <summary>Executes <paramref name="operation" />, retrying driver-transient failures per the policy.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="options">The policy.</param>
    /// <param name="operation">The operation (re-invoked whole on retry).</param>
    /// <param name="resetAsync">Invoked before each retry — the hook that disposes the broken connection.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public static async Task<T> ExecuteAsync<T>(RelationalResilienceOptions options,
        Func<CancellationToken, Task<T>> operation, Func<Task> resetAsync, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (DbException exception) when (exception.IsTransient && attempt < options.MaxRetries)
            {
                Activity.Current?.SetTag("equantic.retries", attempt + 1);
                await resetAsync().ConfigureAwait(false);

                var backoff = options.BaseDelay * Math.Pow(2, attempt);
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)Math.Max(1, options.BaseDelay.TotalMilliseconds / 2)));
                await Task.Delay(backoff + jitter, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
