using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Locking;
using Microsoft.Extensions.Logging;

namespace eQuantic.Core.Data.MongoDb.Repository;

/// <summary>
///     Named locks on MongoDB, held as leases in the <see cref="MongoNamedLocks.CollectionName" /> collection of this
///     unit of work's database (see <see cref="MongoNamedLocks" />). A lease is written outside any transaction, so
///     other holders see it at once; one taken inside an explicit transaction is deleted when the transaction ends.
/// </summary>
public abstract partial class MongoUnitOfWork : ILockingUnitOfWork
{
    private NamedLockSet? _namedLocks;

    /// <inheritdoc />
    /// <remarks>
    ///     A lease lasts <see cref="NamedLockOptions.LeaseDuration" /> past its last renewal, renewed every
    ///     <see cref="NamedLockOptions.RenewalInterval" /> while held: a holder that dies keeps the lock until then.
    /// </remarks>
    public Task<INamedLock> AcquireLockAsync(string name, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var logger = (ServiceProvider.GetService(typeof(ILoggerFactory)) as ILoggerFactory)?.CreateLogger("eQuantic.Core.Data.mongodb.Command");
        var locks = _namedLocks ??= new NamedLockSet("mongodb",
            ServiceProvider.GetService(typeof(NamedLockOptions)) as NamedLockOptions, logger);
        return locks.AcquireAsync(name, timeout, _session is { IsInTransaction: true },
            () => new LeaseLockAttempt(MongoNamedLocks.Store(Database, logger), name, locks.Options), cancellationToken);
    }

    /// <summary>Ends the acquisitions made inside the transaction that just ended, deleting what nothing else holds.</summary>
    private Task EndTransactionLocksAsync() => _namedLocks?.TransactionEndedAsync() ?? Task.CompletedTask;

    /// <summary>Releases every named lock this unit of work holds (it is being disposed).</summary>
    private void ReleaseNamedLocks() => _namedLocks?.ReleaseAll();
}
