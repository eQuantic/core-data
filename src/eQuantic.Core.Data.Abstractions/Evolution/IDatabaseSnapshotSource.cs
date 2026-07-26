using System.Threading;
using System.Threading.Tasks;

namespace eQuantic.Core.Data.Evolution;

/// <summary>
///     Describes both sides of a drift check: what the model says the store should hold, and what it actually
///     holds. One implementation produces both, in one vocabulary, so a difference between them is a real
///     difference and not two ways of spelling the same type.
/// </summary>
public interface IDatabaseSnapshotSource
{
    /// <summary>The store.</summary>
    string Provider { get; }

    /// <summary>What the model says the store should hold.</summary>
    DatabaseSnapshot Expect();

    /// <summary>
    ///     What the store actually holds, for the collections the model maps. Collections it does not map are not
    ///     read: a database is usually shared, and reporting every table an application does not know about would
    ///     bury the findings that matter.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<DatabaseSnapshot> ObserveAsync(CancellationToken cancellationToken = default);
}
