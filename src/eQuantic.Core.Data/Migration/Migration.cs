using System.Threading;
using System.Threading.Tasks;

namespace eQuantic.Core.Data.Migration;

/// <summary>
///     The base class a migration derives from. Mark it with <see cref="MigrationAttribute" /> and declare
///     the work fluently in <see cref="Up" />; the <see cref="IMigrationRunner" /> discovers it, orders it by
///     timestamp, and applies the pending ones through the provider's <see cref="IMigrationExecutor" />.
/// </summary>
public abstract class Migration
{
    /// <summary>Declares the migration's operations against the fluent, typed <paramref name="migration" /> builder.</summary>
    /// <param name="migration">The migration builder.</param>
    public abstract void Up(IMigrationBuilder migration);
}

/// <summary>
///     Applies the provider-agnostic <see cref="MigrationOperation" />s declared by a migration to the
///     underlying store (creating collections/containers and indexes, converting and renaming fields,
///     running data updates).
/// </summary>
public interface IMigrationExecutor
{
    /// <summary>Applies the operations, in order.</summary>
    /// <param name="operations">The declared operations.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task ApplyAsync(System.Collections.Generic.IReadOnlyList<MigrationOperation> operations, CancellationToken cancellationToken = default);
}

/// <summary>
///     The provider-specific context handed to a <see cref="RunOperation" /> escape hatch. Providers expose
///     their native handle on it (e.g. the MongoDB database) through their own extension.
/// </summary>
public interface IMigrationExecutionContext;
