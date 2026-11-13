using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using eQuantic.Core.Data.Diagnostics;
using Microsoft.Extensions.Logging;

namespace eQuantic.Core.Data.Repository.Locking;

/// <summary>
///     The named locks one unit of work holds, and the rules every provider shares around its store's own mechanism:
///     <list type="bullet">
///         <item>
///             <b>Waiting.</b> An attempt is repeated until it answers a lock, the timeout passes
///             (<see cref="TimeoutException" />) or the token is cancelled (<see cref="OperationCanceledException" />).
///             A store that waits on the server is asked in slices of <see cref="NamedLockOptions.PollInterval" />; a
///             store that answers at once is polled, with a pause that grows to that interval. A lock taken while the
///             token was being cancelled is released before the cancellation is reported, so none is left behind.
///         </item>
///         <item>
///             <b>Reentrancy.</b> A name the unit of work already holds is held again at once, with no second store
///             lock: the acquisitions are counted, and the store's lock is released when the last of them ends.
///         </item>
///         <item>
///             <b>Transactions.</b> An acquisition made inside an explicit transaction ends with the transaction
///             (<see cref="TransactionEndedAsync" />), whether or not its handle was disposed first; one made outside
///             ends when its handle is disposed. Disposing the unit of work releases everything (<see cref="ReleaseAll" />).
///         </item>
///     </list>
///     <para>
///         <b>Provider SPI</b>: a unit of work creates one and supplies the attempts; application code never needs it.
///     </para>
/// </summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class NamedLockSet
{
    private static readonly TimeSpan FirstPause = TimeSpan.FromMilliseconds(5);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _held = new(StringComparer.Ordinal);
    private readonly string _system;
    private readonly ILogger? _logger;
    private bool _closed;

    /// <summary>Creates the set of a unit of work.</summary>
    /// <param name="system">The store's <c>db.system</c> name (activities and logs).</param>
    /// <param name="options">The registered options, or <c>null</c> for the defaults.</param>
    /// <param name="logger">Where a lock that could not be released is reported, or <c>null</c>.</param>
    public NamedLockSet(string system, NamedLockOptions? options, ILogger? logger = null)
    {
        _system = system;
        _logger = logger;
        Options = options ?? new NamedLockOptions();
        Options.Validate();
    }

    /// <summary>The options in force.</summary>
    public NamedLockOptions Options { get; }

    /// <summary>
    ///     Acquires <paramref name="name" />: at once when the unit of work already holds it, otherwise through
    ///     attempts from <paramref name="begin" /> until one answers a lock.
    /// </summary>
    /// <param name="name">The lock's name.</param>
    /// <param name="timeout">How long to wait (zero tries once; <see cref="Timeout.InfiniteTimeSpan" /> waits until cancelled).</param>
    /// <param name="inTransaction">Whether an explicit transaction is open: the acquisition then ends with it.</param>
    /// <param name="begin">Starts an acquisition (called only when the name is not already held).</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The handle.</returns>
    public async Task<INamedLock> AcquireAsync(string name, TimeSpan timeout, bool inTransaction,
        Func<NamedLockAttempt> begin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0)
        {
            throw new ArgumentException("A named lock needs a name.", nameof(name));
        }

        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout,
                "A timeout is zero (try once), positive, or Timeout.InfiniteTimeSpan.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfClosed();
            if (_held.TryGetValue(name, out var held))
            {
                return held.Hold(inTransaction);
            }
        }

        using var activity = DataActivitySource.Instance.StartActivity($"{_system}.acquire_lock", ActivityKind.Client);
        activity?.SetTag("db.system", _system);
        activity?.SetTag("equantic.lock_in_transaction", inTransaction);

        NativeLock native;
        var attempt = begin();
        await using (attempt.ConfigureAwait(false))
        {
            native = await WaitAsync(name, timeout, attempt, cancellationToken).ConfigureAwait(false);
        }

        Entry? entry = null;
        lock (_gate)
        {
            if (!_closed && !_held.ContainsKey(name))
            {
                entry = new Entry(this, name, native);
                _held.Add(name, entry);
            }
        }

        if (entry is null)
        {
            // The unit of work was disposed while waiting, or another flow on it took the same name meanwhile
            // (a unit of work serves one flow at a time; this keeps the store's lock from leaking either way).
            await ReleaseQuietlyAsync(native, name).ConfigureAwait(false);
            lock (_gate)
            {
                ThrowIfClosed();
                return _held[name].Hold(inTransaction);
            }
        }

        lock (_gate)
        {
            return entry.Hold(inTransaction);
        }
    }

    /// <summary>
    ///     Ends every acquisition made inside the transaction that just committed or rolled back, and releases the
    ///     store's locks nothing else holds. Called by the unit of work once the transaction is over.
    /// </summary>
    public async Task TransactionEndedAsync()
    {
        List<Entry> released = [];
        lock (_gate)
        {
            foreach (var entry in _held.Values)
            {
                entry.TransactionHolds = 0;
                if (entry.Unheld || entry.Native.EndsWithTransaction)
                {
                    released.Add(entry);
                }
            }

            foreach (var entry in released)
            {
                _held.Remove(entry.Name);
                entry.Ended = true;
            }
        }

        foreach (var entry in released)
        {
            await ReleaseQuietlyAsync(entry.Native, entry.Name).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Releases every lock the unit of work holds, because it is being disposed; later acquisitions are refused.
    ///     Synchronous (a unit of work's <c>Dispose</c> is), and a lock that cannot be released is reported, not thrown.
    /// </summary>
    public void ReleaseAll()
    {
        List<Entry> released;
        lock (_gate)
        {
            _closed = true;
            released = [.. _held.Values];
            _held.Clear();
            foreach (var entry in released)
            {
                entry.Ended = true;
            }
        }

        foreach (var entry in released)
        {
            // Off the caller's synchronization context: the release is asynchronous all the way down.
            Task.Run(() => ReleaseQuietlyAsync(entry.Native, entry.Name)).GetAwaiter().GetResult();
        }
    }

    private async Task<NativeLock> WaitAsync(string name, TimeSpan timeout, NamedLockAttempt attempt,
        CancellationToken cancellationToken)
    {
        var infinite = timeout == Timeout.InfiniteTimeSpan;
        var clock = Stopwatch.StartNew();
        var pause = FirstPause < Options.PollInterval ? FirstPause : Options.PollInterval;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = infinite ? Options.PollInterval : Max(timeout - clock.Elapsed, TimeSpan.Zero);
            var wait = attempt.WaitsOnStore ? Min(remaining, Options.PollInterval) : TimeSpan.Zero;
            var started = clock.Elapsed;

            var native = await attempt.TryAsync(wait, cancellationToken).ConfigureAwait(false);
            if (native is not null)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    await ReleaseQuietlyAsync(native, name).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return native;
            }

            var left = infinite ? TimeSpan.MaxValue : timeout - clock.Elapsed;
            if (left <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    $"The named lock '{name}' was not acquired within {Describe(timeout)}: another holder kept it.");
            }

            TimeSpan delay;
            if (attempt.WaitsOnStore)
            {
                // A store that answered before its wait was up (one that counts its wait in whole seconds) is not
                // asked again straight away.
                delay = wait - (clock.Elapsed - started);
            }
            else
            {
                delay = TimeSpan.FromTicks((long)(pause.Ticks * (0.5 + Random.Shared.NextDouble() / 2)));
                pause = Min(pause + pause, Options.PollInterval);
            }

            delay = Min(delay, left);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ReleaseQuietlyAsync(NativeLock native, string name)
    {
        try
        {
            await native.ReleaseAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The store could not be reached to let go: a session lock dies with its broken session, and a lease
            // runs out on its own. Nothing the caller can do changes that, so it is reported rather than thrown.
            _logger?.Log(LogLevel.Warning, DataEvents.LockReleaseFailed, exception,
                "The named lock '{Name}' could not be released explicitly; the store frees it when its session ends or its lease runs out",
                name);
        }
    }

    private async ValueTask EndHandleAsync(Handle handle)
    {
        NativeLock? released = null;
        lock (_gate)
        {
            if (handle.Disposed)
            {
                return;
            }

            handle.Disposed = true;
            var entry = handle.Entry;
            if (handle.HeldUntilTransactionEnds || entry.Ended)
            {
                return;
            }

            entry.Handles--;
            if (entry.Unheld)
            {
                _held.Remove(entry.Name);
                entry.Ended = true;
                released = entry.Native;
            }
        }

        if (released is not null)
        {
            await ReleaseQuietlyAsync(released, handle.Name).ConfigureAwait(false);
        }
    }

    private void ThrowIfClosed()
    {
        if (_closed)
        {
            throw new ObjectDisposedException(nameof(NamedLockSet), "The unit of work holding these locks was disposed.");
        }
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;

    private static string Describe(TimeSpan timeout) =>
        timeout < TimeSpan.FromSeconds(1) ? $"{timeout.TotalMilliseconds:0} ms" : $"{timeout.TotalSeconds:0.###} s";

    /// <summary>One name the unit of work holds: the store's lock and the acquisitions still holding it.</summary>
    private sealed class Entry(NamedLockSet set, string name, NativeLock native)
    {
        public string Name { get; } = name;

        public NativeLock Native { get; } = native;

        /// <summary>Acquisitions made outside a transaction whose handles are not disposed yet.</summary>
        public int Handles { get; set; }

        /// <summary>Acquisitions made inside the open transaction.</summary>
        public int TransactionHolds { get; set; }

        /// <summary>Whether the store's lock was released (or forgotten with its transaction).</summary>
        public bool Ended { get; set; }

        public bool Unheld => Handles == 0 && TransactionHolds == 0;

        public INamedLock Hold(bool inTransaction)
        {
            if (inTransaction)
            {
                TransactionHolds++;
            }
            else
            {
                Handles++;
            }

            return new Handle(set, this, inTransaction);
        }
    }

    private sealed class Handle(NamedLockSet set, Entry entry, bool inTransaction) : INamedLock
    {
        public Entry Entry { get; } = entry;

        public bool Disposed { get; set; }

        public string Name => Entry.Name;

        public bool HeldUntilTransactionEnds { get; } = inTransaction;

        public CancellationToken Lost => Entry.Native.Lost;

        public ValueTask DisposeAsync() => set.EndHandleAsync(this);
    }
}
