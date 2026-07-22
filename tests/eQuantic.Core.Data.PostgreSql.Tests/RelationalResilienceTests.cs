using System.Data.Common;
using eQuantic.Core.Data.Relational;

namespace eQuantic.Core.Data.PostgreSql.Tests;

/// <summary>
///     Unit tests for the transient-retry loop — no server involved. Proves the policy retries exactly what the
///     driver marks transient, resets the connection between attempts, and gives up honestly.
/// </summary>
[TestFixture]
public sealed class RelationalResilienceTests
{
    private sealed class FakeDbException(bool transient) : DbException
    {
        public override bool IsTransient { get; } = transient;
    }

    private static RelationalResilienceOptions Fast(int maxRetries) => new()
    {
        MaxRetries = maxRetries,
        BaseDelay = TimeSpan.FromMilliseconds(1),
    };

    [Test]
    public async Task Transient_failures_retry_with_a_connection_reset_until_success()
    {
        var attempts = 0;
        var resets = 0;

        var result = await RelationalResilience.ExecuteAsync(Fast(3), _ =>
        {
            attempts++;
            return attempts < 3 ? throw new FakeDbException(transient: true) : Task.FromResult(42);
        }, () =>
        {
            resets++;
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.That(result, Is.EqualTo(42));
        Assert.That(attempts, Is.EqualTo(3), "two transient failures, then success");
        Assert.That(resets, Is.EqualTo(2), "the broken connection reset before each retry");
    }

    [Test]
    public void Non_transient_failures_never_retry()
    {
        var attempts = 0;

        Assert.That(async () => await RelationalResilience.ExecuteAsync<int>(Fast(3), _ =>
            {
                attempts++;
                throw new FakeDbException(transient: false);
            }, () => Task.CompletedTask, CancellationToken.None),
            Throws.TypeOf<FakeDbException>());
        Assert.That(attempts, Is.EqualTo(1), "a non-transient failure propagates immediately");
    }

    [Test]
    public void Exhausted_retries_rethrow_the_last_transient_failure()
    {
        var attempts = 0;

        Assert.That(async () => await RelationalResilience.ExecuteAsync<int>(Fast(2), _ =>
            {
                attempts++;
                throw new FakeDbException(transient: true);
            }, () => Task.CompletedTask, CancellationToken.None),
            Throws.TypeOf<FakeDbException>());
        Assert.That(attempts, Is.EqualTo(3), "the first attempt plus MaxRetries, then honest surrender");
    }
}
