using System;

namespace eQuantic.Core.Data.Repository;

/// <summary>
///     Thrown when a commit's optimistic-concurrency check fails: at least one staged update or delete matched
///     no row, because another writer changed (or removed) it since it was read. The whole flush rolled back —
///     nothing was applied. Reload the entities, reapply the changes and commit again.
/// </summary>
public sealed class ConcurrencyConflictException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="expected">The number of rows the flush expected to affect.</param>
    /// <param name="affected">The number of rows actually affected.</param>
    public ConcurrencyConflictException(long expected, long affected)
        : base($"The commit expected to affect {expected} row(s) but affected {affected} — another writer changed or " +
               "removed at least one of them since it was read. The flush rolled back; reload, reapply and retry.")
    {
        Expected = expected;
        Affected = affected;
    }

    /// <summary>The number of rows the flush expected to affect.</summary>
    public long Expected { get; }

    /// <summary>The number of rows actually affected.</summary>
    public long Affected { get; }
}
