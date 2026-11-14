using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Locking;
using Microsoft.Extensions.Logging;

namespace eQuantic.Core.Data.CosmosDb.Repository;

/// <summary>
///     Named locks on Cosmos DB, held as leases in the <see cref="CosmosNamedLocks.ContainerName" /> container of this
///     unit of work's database (see <see cref="CosmosNamedLocks" />). A lease is written at once, outside the deferred
///     transactional batch, so other holders see it; one taken inside an explicit transaction is deleted once the
///     batch has run (or been discarded).
/// </summary>
public abstract partial class CosmosUnitOfWork : ILockingUnitOfWork
{
    private NamedLockSet? _namedLocks;

    /// <inheritdoc />
    /// <remarks>
    ///     A lease lasts <see cref="NamedLockOptions.LeaseDuration" /> past its last renewal (up to a second more:
    ///     the server's timestamps count whole seconds), renewed every <see cref="NamedLockOptions.RenewalInterval" />
    ///     while held: a holder that dies keeps the lock until then.
    /// </remarks>
    public Task<INamedLock> AcquireLockAsync(string name, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var locks = _namedLocks ??= new NamedLockSet("cosmosdb",
            ServiceProvider.GetService(typeof(NamedLockOptions)) as NamedLockOptions,
            (ServiceProvider.GetService(typeof(ILoggerFactory)) as ILoggerFactory)?.CreateLogger("eQuantic.Core.Data.cosmosdb.Command"));
        return locks.AcquireAsync(name, timeout, _inTransaction,
            () => new LeaseLockAttempt(CosmosNamedLocks.Store(Database), name, locks.Options), cancellationToken);
    }

    /// <summary>Ends the acquisitions made inside the transaction that just ended, deleting what nothing else holds.</summary>
    private Task EndTransactionLocksAsync() => _namedLocks?.TransactionEndedAsync() ?? Task.CompletedTask;

    /// <summary>Releases every named lock this unit of work holds (it is being disposed).</summary>
    private void ReleaseNamedLocks() => _namedLocks?.ReleaseAll();
}
