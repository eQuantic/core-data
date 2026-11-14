using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Locking;

namespace eQuantic.Core.Data.Cassandra.Repository;

/// <summary>
///     Named locks on Cassandra, held as leases in the <see cref="CassandraNamedLocks.TableName" /> table of this unit
///     of work's keyspace (see <see cref="CassandraNamedLocks" />). A lease is written at once, outside the deferred
///     <c>LOGGED BATCH</c>, so other holders see it; one taken inside an explicit transaction is deleted once the batch
///     has run (or been discarded).
/// </summary>
public abstract partial class CassandraUnitOfWork : ILockingUnitOfWork
{
    private NamedLockSet? _namedLocks;

    /// <inheritdoc />
    /// <remarks>
    ///     A lease lasts <see cref="NamedLockOptions.LeaseDuration" /> (in whole seconds) past its last renewal, renewed
    ///     every <see cref="NamedLockOptions.RenewalInterval" /> while held: a holder that dies keeps the lock until then.
    /// </remarks>
    public Task<INamedLock> AcquireLockAsync(string name, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var locks = _namedLocks ??= new NamedLockSet("cassandra",
            ServiceProvider.GetService(typeof(NamedLockOptions)) as NamedLockOptions, CommandLogger);
        return locks.AcquireAsync(name, timeout, _inTransaction,
            () => new LeaseLockAttempt(CassandraNamedLocks.Store(Session, CommandLogger, SensitiveLogging), name, locks.Options),
            cancellationToken);
    }

    /// <summary>Ends the acquisitions made inside the transaction that just ended, deleting what nothing else holds.</summary>
    private Task EndTransactionLocksAsync() => _namedLocks?.TransactionEndedAsync() ?? Task.CompletedTask;

    /// <summary>Releases every named lock this unit of work holds (it is being disposed).</summary>
    private void ReleaseNamedLocks() => _namedLocks?.ReleaseAll();
}
