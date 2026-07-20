using System.Threading;
using System.Threading.Tasks;

namespace eQuantic.Core.Data.Migration;

/// <summary>
/// A single, ordered data-store migration. Mark the implementing class with <see cref="MigrationAttribute" />
/// to give it a title and the timestamp the runner orders by; the <see cref="IMigrationRunner" /> discovers,
/// orders and applies the pending ones exactly once, recording each in the <see cref="IMigrationHistory" />.
/// </summary>
/// <remarks>
/// Unlike relational EF Core migrations, these run against document stores and can do what those cannot —
/// create or configure collections/containers, add single or composite indexes, and transform existing
/// documents. The store handle is supplied to the implementation (typically through its constructor).
/// </remarks>
public interface IMigration
{
    /// <summary>
    /// Applies the migration. Should be written to be safe to run against existing data (the runner
    /// guarantees it runs at most once, but a defensive implementation survives partial prior runs).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task UpAsync(CancellationToken cancellationToken = default);
}
