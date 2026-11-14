using System.Data.Common;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Locking;

namespace eQuantic.Core.Data.Relational.Repository;

/// <summary>
///     Named locks on the relational engine, through the dialect's own mechanism (<see cref="SqlDialect.NamedLocks" />):
///     inside an explicit transaction the lock is taken on the transaction, and the transaction's end releases it;
///     outside one, it is taken for a session on a connection of its own, pinned by the handle until it is disposed.
/// </summary>
public abstract partial class RelationalUnitOfWork : ILockingUnitOfWork
{
    private NamedLockSet? _namedLocks;

    /// <inheritdoc />
    /// <remarks>
    ///     Outside a transaction each held name pins one pooled connection until its handle (or this unit of work) is
    ///     disposed, so the session that holds the lock is never handed to anyone else meanwhile.
    /// </remarks>
    public Task<INamedLock> AcquireLockAsync(string name, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var mechanism = Dialect.NamedLocks ?? throw new NotSupportedException(
            $"{Dialect.GetType().Name} declares no named-lock mechanism (SqlDialect.NamedLocks); this engine cannot hold a named lock.");

        _namedLocks ??= new NamedLockSet(Dialect.System,
            ServiceProvider.GetService(typeof(NamedLockOptions)) as NamedLockOptions, CommandLogger);
        var inTransaction = _transaction is not null;
        return _namedLocks.AcquireAsync(name, timeout, inTransaction,
            () => inTransaction
                ? new TransactionLockAttempt(this, mechanism, name)
                : new SessionLockAttempt(this, mechanism, name),
            cancellationToken);
    }

    /// <summary>
    ///     Acquires <paramref name="name" /> for the transaction the unit of work's statements run in now: the explicit
    ///     one, or the transaction of its own an immediate write opened (<see cref="AtomicallyAsync{T}" />). Either way the
    ///     lock is taken on the transaction's connection and released when that transaction ends, committed or rolled
    ///     back, as a lock taken inside an explicit transaction is.
    /// </summary>
    /// <param name="name">The lock's name.</param>
    /// <param name="timeout">How long to wait (<see cref="Timeout.InfiniteTimeSpan" /> waits until cancelled).</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="TimeoutException">Another holder kept the lock for the whole timeout.</exception>
    internal async Task LockForTransactionAsync(string name, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var mechanism = Dialect.NamedLocks ?? throw new NotSupportedException(
            $"{Dialect.GetType().Name} declares no named-lock mechanism (SqlDialect.NamedLocks); this engine cannot hold a named lock.");
        if (_transaction is null && _localTransaction is null)
        {
            throw new InvalidOperationException("A lock for the transaction is taken only while a transaction is open.");
        }

        _namedLocks ??= new NamedLockSet(Dialect.System,
            ServiceProvider.GetService(typeof(NamedLockOptions)) as NamedLockOptions, CommandLogger);

        // Set before the wait, so the immediate write's transaction ends whatever the wait took (a lock taken while
        // the wait was being cancelled is released by the set itself).
        _localTransactionLocks |= _transaction is null;

        // Held by the transaction, not by the handle: disposing it would change nothing, and the transaction's end
        // releases the lock.
        _ = await _namedLocks.AcquireAsync(name, timeout, inTransaction: true,
            () => new TransactionLockAttempt(this, mechanism, name), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether a lock was taken for the transaction of an immediate write (<see cref="LockForTransactionAsync" />).</summary>
    private bool _localTransactionLocks;

    /// <summary>
    ///     Ends the locks taken for the transaction of an immediate write, once that transaction is over and no longer
    ///     bound to the commands (a lock released by statement, MySQL's <c>RELEASE_LOCK</c>, runs outside it).
    /// </summary>
    private Task EndLocalTransactionLocksAsync()
    {
        if (!_localTransactionLocks)
        {
            return Task.CompletedTask;
        }

        _localTransactionLocks = false;
        return EndTransactionLocksAsync();
    }

    /// <summary>Ends the acquisitions made inside the transaction that just ended, releasing what nothing else holds.</summary>
    private Task EndTransactionLocksAsync() => _namedLocks?.TransactionEndedAsync() ?? Task.CompletedTask;

    /// <summary>Releases every named lock this unit of work holds (it is being disposed).</summary>
    private void ReleaseNamedLocks() => _namedLocks?.ReleaseAll();

    /// <summary>Runs one lock statement on a command and answers its single value.</summary>
    private async Task<object?> ScalarAsync(DbCommand command, SqlLockStatement statement)
    {
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = statement.Sql;
            for (var index = 0; index < statement.Parameters.Count; index++)
            {
                var value = Dialect.BindValue(statement.Parameters[index]);
                var parameter = command.CreateParameter();
                parameter.ParameterName = "p" + index;
                parameter.Value = value ?? DBNull.Value;
                Dialect.ConfigureParameter(parameter, value);
                command.Parameters.Add(parameter);
            }

            // Not cancelled once sent: a statement cut off mid-flight may or may not have taken the lock. Its wait is
            // bounded by the server instead (a slice of the poll interval, or no wait at all).
            var answer = await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
            return answer is DBNull ? null : answer;
        }
    }

    /// <summary>A command on this unit of work's connection, inside its open transaction, logged like every other.</summary>
    private async Task<DbCommand> TransactionCommandAsync(CancellationToken cancellationToken) =>
        await CommandAsync(string.Empty, [], cancellationToken).ConfigureAwait(false);

    /// <summary>A command on a pinned connection, logged like every other.</summary>
    private DbCommand PinnedCommand(DbConnection connection) =>
        new Diagnostics.LoggingDbCommand(connection.CreateCommand(), CommandLogger, Dialect.System,
            Conventions.EnableSensitiveDataLogging);

    /// <summary>An acquisition inside the open transaction, on its connection.</summary>
    private sealed class TransactionLockAttempt(RelationalUnitOfWork unitOfWork, SqlNamedLocks mechanism, string name)
        : NamedLockAttempt
    {
        public override bool WaitsOnStore => mechanism.WaitsOnServer;

        public override async Task<NativeLock?> TryAsync(TimeSpan wait, CancellationToken cancellationToken)
        {
            var command = await unitOfWork.TransactionCommandAsync(cancellationToken).ConfigureAwait(false);
            var answer = await unitOfWork.ScalarAsync(command, mechanism.Acquire(name, inTransaction: true, wait)).ConfigureAwait(false);
            return mechanism.Acquired(answer, name) ? new TransactionLock(unitOfWork, mechanism, name) : null;
        }
    }

    /// <summary>A lock held by the transaction: the server releases it at the transaction's end, or the engine does right after.</summary>
    private sealed class TransactionLock(RelationalUnitOfWork unitOfWork, SqlNamedLocks mechanism, string name) : NativeLock
    {
        public override bool EndsWithTransaction => mechanism.ReleasedWithTransaction;

        public override async Task ReleaseAsync()
        {
            if (mechanism.ReleasedWithTransaction)
            {
                return;
            }

            // The transaction's session holds it (MySQL's GET_LOCK): released on that connection, once the
            // transaction is over, or before it is disposed with the unit of work.
            var command = await unitOfWork.TransactionCommandAsync(CancellationToken.None).ConfigureAwait(false);
            await unitOfWork.ScalarAsync(command, mechanism.Release(name, inTransaction: true)).ConfigureAwait(false);
        }
    }

    /// <summary>An acquisition for a session, on a connection of its own that the lock pins once it is taken.</summary>
    private sealed class SessionLockAttempt(RelationalUnitOfWork unitOfWork, SqlNamedLocks mechanism, string name)
        : NamedLockAttempt
    {
        private DbConnection? _connection;

        public override bool WaitsOnStore => mechanism.WaitsOnServer;

        public override async Task<NativeLock?> TryAsync(TimeSpan wait, CancellationToken cancellationToken)
        {
            if (_connection is null)
            {
                var connection = unitOfWork.DataSource.CreateConnection();
                try
                {
                    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                _connection = connection;
            }

            var answer = await unitOfWork.ScalarAsync(unitOfWork.PinnedCommand(_connection),
                mechanism.Acquire(name, inTransaction: false, wait)).ConfigureAwait(false);
            if (!mechanism.Acquired(answer, name))
            {
                return null;
            }

            var pinned = new SessionLock(unitOfWork, mechanism, name, _connection);
            _connection = null;
            return pinned;
        }

        public override async ValueTask DisposeAsync()
        {
            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }
        }
    }

    /// <summary>A lock the session of a pinned connection holds: released on that connection, which then goes back to the pool.</summary>
    private sealed class SessionLock(RelationalUnitOfWork unitOfWork, SqlNamedLocks mechanism, string name, DbConnection connection)
        : NativeLock
    {
        public override async Task ReleaseAsync()
        {
            try
            {
                await unitOfWork.ScalarAsync(unitOfWork.PinnedCommand(connection),
                    mechanism.Release(name, inTransaction: false)).ConfigureAwait(false);
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
