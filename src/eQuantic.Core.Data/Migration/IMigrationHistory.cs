using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace eQuantic.Core.Data.Migration;

/// <summary>
/// Tracks which migrations have already been applied, so the <see cref="IMigrationRunner" /> never applies
/// one twice. A provider implements it over its own store (e.g. a dedicated MongoDB collection or Cosmos
/// container), which also makes migration state visible and queryable.
/// </summary>
public interface IMigrationHistory
{
    /// <summary>
    /// Ensures the backing store for the history exists (idempotent). Called before the first read/record.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task EnsureCreatedAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the identifiers of the migrations already applied.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<IReadOnlyCollection<string>> GetAppliedIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>Records a migration as applied.</summary>
    /// <param name="migration">The applied migration.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task RecordAsync(AppliedMigration migration, CancellationToken cancellationToken = default);
}
