using System.Threading;
using System.Threading.Tasks;

namespace eQuantic.Core.Data.Migration;

/// <summary>
/// Discovers the <see cref="Migration" /> types marked with <see cref="MigrationAttribute" />, orders them
/// by their timestamp, skips the ones already recorded in the <see cref="IMigrationHistory" />, applies the
/// rest in order (recording each as it succeeds) and reports how many ran.
/// </summary>
public interface IMigrationRunner
{
    /// <summary>
    /// Applies every pending migration in timestamp order. Safe to call on every startup — already-applied
    /// migrations are skipped.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of migrations applied by this call.</returns>
    Task<int> RunAsync(CancellationToken cancellationToken = default);
}
