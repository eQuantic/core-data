using System.Data.Common;
using System.Diagnostics;
using System.Linq.Expressions;
using eQuantic.Core.Data.Diagnostics;
using eQuantic.Core.Data.Query;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;

namespace eQuantic.Core.Data.Relational.Repository;

/// <summary>
///     The native relational unit of work over a <see cref="DbDataSource" />. Entity writes are staged and
///     flushed on <see cref="CommitAsync(System.Threading.CancellationToken)" /> as one <see cref="DbBatch" />
///     wrapped in a transaction — the commit is <b>atomic</b> (this is a relational store: a failed write rolls
///     the flush back). Database-generated keys are read back into the entities on insert. An explicit
///     transaction (<see cref="BeginTransactionAsync" />) spans multiple commits: each commit flushes into it —
///     visible to this unit of work's own reads — and <see cref="CommitTransactionAsync" /> makes it durable.
/// </summary>
public abstract class RelationalUnitOfWork : IQueryableUnitOfWork, IUnionQueryRunner
{
    protected readonly IServiceProvider ServiceProvider;
    protected readonly DbDataSource DataSource;

    internal SqlDialect Dialect { get; }
    internal RelationalModel Model { get; }

    private readonly List<PendingWrite> _pending = [];
    private readonly RelationalResilienceOptions? _resilience;
    private DbConnection? _connection;
    private DbTransaction? _transaction;
    private QueryFilters? _queryFilters;
    private bool _queryFiltersResolved;
    private DataConventions? _conventions;

    /// <summary>The active write conventions — the registered <see cref="DataConventions" />, or the defaults.</summary>
    internal DataConventions Conventions =>
        _conventions ??= ServiceProvider.GetService(typeof(DataConventions)) as DataConventions ?? new DataConventions();

    /// <summary>The scope's service provider (handed to per-request convention accessors).</summary>
    internal IServiceProvider Services => ServiceProvider;
    private bool _disposed;

    protected RelationalUnitOfWork(IServiceProvider serviceProvider, DbDataSource dataSource, SqlDialect dialect, RelationalModel model)
    {
        ServiceProvider = serviceProvider;
        DataSource = dataSource;
        Dialect = dialect;
        Model = model;
        _resilience = serviceProvider.GetService(typeof(RelationalResilienceOptions)) as RelationalResilienceOptions;
    }

    /// <summary>
    ///     Runs an operation under the transient-retry policy — a no-op without one, for a commit without the
    ///     commit opt-in, and inside an explicit transaction (a broken transaction cannot be resumed).
    /// </summary>
    internal async Task<T> RetryAsync<T>(bool write, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        var enabled = _resilience is { MaxRetries: > 0 } policy
                      && (!write || policy.RetryCommits)
                      && _transaction is null;
        return enabled
            ? await RelationalResilience.ExecuteAsync(_resilience!, operation, ResetConnectionAsync, cancellationToken).ConfigureAwait(false)
            : await operation(cancellationToken).ConfigureAwait(false);
    }

    private async Task ResetConnectionAsync()
    {
        if (_connection is not null)
        {
            try
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // The connection is already broken; disposal noise carries no information.
            }

            _connection = null;
        }
    }

    internal RelationalEntityConfiguration Configuration<TEntity>() => Model.For(typeof(TEntity));

    /// <summary>The global filter registered for <typeparamref name="TEntity" /> in this scope, or <c>null</c>.</summary>
    internal Expression<Func<TEntity, bool>>? GlobalFilter<TEntity>() where TEntity : class =>
        (Expression<Func<TEntity, bool>>?)GlobalFilter(typeof(TEntity));

    /// <summary>The global filter registered for <paramref name="entityType" /> in this scope, or <c>null</c> — union branches resolve per branch.</summary>
    /// <remarks>A soft-delete entity's live-rows filter is ANDed in by convention.</remarks>
    internal LambdaExpression? GlobalFilter(Type entityType)
    {
        if (!_queryFiltersResolved)
        {
            _queryFilters = ServiceProvider.GetService(typeof(QueryFilters)) as QueryFilters;
            _queryFiltersResolved = true;
        }

        return EntityLifecycle.And(
            _queryFilters?.FilterFor(entityType, ServiceProvider),
            EntityLifecycle.SoftDeleteFilter(entityType, Conventions));
    }

    // -------------------------------------------------------------- union reads

    /// <inheritdoc />
    /// <remarks>
    ///     Renders one <c>SELECT</c> per branch — each branch's filters (the entity's global filter included
    ///     unless the branch opted out) push into its <c>WHERE</c> — combined with <c>UNION</c>/<c>UNION ALL</c>
    ///     on the store; ordering and paging apply to the combined rows. A branch filter SQL cannot express is
    ///     rejected with guidance: a union cannot run part of a branch client-side.
    /// </remarks>
    public async Task<IReadOnlyList<TResult>> UnionAsync<TResult>(UnionQuery<TResult> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (sql, parameters, shape) = RelationalUnion.Build(Dialect, Model, query, GlobalFilter);

        using var activity = DataActivitySource.Instance.StartActivity($"{Dialect.System}.union", ActivityKind.Client);
        if (activity is not null)
        {
            activity.SetTag("db.system", Dialect.System);
            activity.SetTag("equantic.union_branches", query.Branches.Count);
            activity.SetTag("equantic.union_all", query.All);
        }

        var projector = new RelationalProjector<TResult>(
            shape.Bindings.Select(binding => binding.Target).ToList(), shape.ConstructorProjection);
        return await RetryAsync<IReadOnlyList<TResult>>(write: false, async _ =>
        {
            var results = new List<TResult>();
            await using var command = await CommandAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var values = new object?[shape.Bindings.Count];
                for (var index = 0; index < values.Length; index++)
                {
                    values[index] = RelationalMaterializer.ChangeValue(
                        reader.IsDBNull(index) ? null : reader.GetValue(index), projector.TargetType(index));
                }

                results.Add(projector.Create(values));
            }

            return results;
        }, cancellationToken).ConfigureAwait(false);
    }

    // -------------------------------------------------------------- connection / command plumbing

    internal async ValueTask<DbConnection> ConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is null)
        {
            _connection = DataSource.CreateConnection();
            await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        return _connection;
    }

    /// <summary>Creates a command bound to this unit of work's connection and (when open) its transaction.</summary>
    internal async ValueTask<DbCommand> CommandAsync(string sql, IReadOnlyList<object?> parameters, CancellationToken cancellationToken)
    {
        var connection = await ConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _transaction;
        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "p" + index;
            parameter.Value = parameters[index] ?? DBNull.Value;
            Dialect.ConfigureParameter(parameter, parameters[index]);
            command.Parameters.Add(parameter);
        }

        return command;
    }

    // -------------------------------------------------------------- write staging

    internal void StageInsert<TEntity>(TEntity item) where TEntity : class
    {
        EntityLifecycle.StampForInsert(item, Conventions, ServiceProvider);
        _pending.Add(new PendingWrite(Configuration<TEntity>(), PendingWriteKind.Insert, item));
    }

    internal void StageUpdate<TEntity>(TEntity item) where TEntity : class
    {
        EntityLifecycle.StampForUpdate(item, Conventions, ServiceProvider);
        _pending.Add(new PendingWrite(Configuration<TEntity>(), PendingWriteKind.Update, item));
    }

    internal void StageDelete<TEntity>(TEntity item) where TEntity : class =>
        _pending.Add(EntityLifecycle.TrySoftDelete(item, Conventions, ServiceProvider)
            ? new PendingWrite(Configuration<TEntity>(), PendingWriteKind.Update, item)
            : new PendingWrite(Configuration<TEntity>(), PendingWriteKind.Delete, item));

    // -------------------------------------------------------------- commit (atomic flush)

    public int Commit() => CommitAsync().GetAwaiter().GetResult();

    public int Commit(Action<SaveOptions> options) => Commit();

    public Task<int> CommitAsync(Action<SaveOptions> options, CancellationToken cancellationToken = default) => CommitAsync(cancellationToken);

    public async Task<int> CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_pending.Count == 0)
        {
            return 0;
        }

        var writes = _pending.ToList();
        _pending.Clear();

        using var activity = DataActivitySource.Instance.StartActivity($"{Dialect.System}.commit", ActivityKind.Client);
        activity?.SetTag("db.system", Dialect.System);
        activity?.SetTag("equantic.writes", writes.Count);

        // Behind the RetryCommits opt-in, a transient failure re-runs the whole flush (its transaction rolled back).
        return await RetryAsync(write: true, _ => FlushAsync(writes, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> FlushAsync(List<PendingWrite> writes, CancellationToken cancellationToken)
    {
        var connection = await ConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Inside an explicit transaction the flush joins it (durable on CommitTransactionAsync); otherwise the
        // flush runs in its own transaction — a relational commit is atomic.
        var local = _transaction is null
            ? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        try
        {
            await using var batch = connection.CreateBatch();
            batch.Transaction = _transaction ?? local;

            var returning = new List<PendingWrite>();
            var bumped = new List<(PendingWrite Write, object NewVersion)>();
            foreach (var write in writes)
            {
                var command = batch.CreateBatchCommand();
                if (Render(write, command, bumped))
                {
                    returning.Add(write);
                }

                batch.BatchCommands.Add(command);
            }

            long affected;
            if (returning.Count == 0)
            {
                affected = await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    foreach (var write in returning)
                    {
                        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            AssignGeneratedKey(write, reader.GetValue(0));
                        }

                        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                    }

                    await reader.CloseAsync().ConfigureAwait(false);
                    affected = reader.RecordsAffected;
                }
            }

            // The optimistic-concurrency check: every staged write targets exactly one row, so a shortfall means
            // a versioned row changed (or vanished) under us. Nothing is durably applied — this flush rolls back.
            if (bumped.Count > 0 && affected >= 0 && affected < writes.Count)
            {
                throw new ConcurrencyConflictException(writes.Count, affected);
            }

            foreach (var (write, newVersion) in bumped)
            {
                write.Configuration.ConcurrencyToken!.Property.SetValue(write.Entity, newVersion);
            }

            if (local is not null)
            {
                await local.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            if (local is not null)
            {
                await local.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (local is not null)
            {
                await local.DisposeAsync().ConfigureAwait(false);
            }
        }

        return writes.Count;
    }

    public int CommitAndRefreshChanges() => Commit();
    public int CommitAndRefreshChanges(Action<SaveOptions> options) => Commit();
    public Task<int> CommitAndRefreshChangesAsync(CancellationToken cancellationToken = default) => CommitAsync(cancellationToken);
    public Task<int> CommitAndRefreshChangesAsync(Action<SaveOptions> options, CancellationToken cancellationToken = default) => CommitAsync(cancellationToken);

    /// <summary>Discards every staged (uncommitted) write.</summary>
    public void RollbackChanges() => _pending.Clear();

    public virtual SaveOptions GetSaveOptions() => new();

    // -------------------------------------------------------------- explicit transactions

    /// <summary>Opens a transaction: subsequent commits flush into it (visible to this unit of work's reads) until it is committed or rolled back.</summary>
    public async Task BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is null)
        {
            var connection = await ConnectionAsync(cancellationToken).ConfigureAwait(false);
            _transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Flushes any staged writes into the transaction and makes it durable.</summary>
    public async Task CommitTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is null)
        {
            return;
        }

        await CommitAsync(cancellationToken).ConfigureAwait(false);
        await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await _transaction.DisposeAsync().ConfigureAwait(false);
        _transaction = null;
    }

    /// <summary>Rolls the transaction back and discards staged writes.</summary>
    public async Task RollbackTransactionAsync(CancellationToken cancellationToken = default)
    {
        _pending.Clear();
        if (_transaction is not null)
        {
            await _transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            await _transaction.DisposeAsync().ConfigureAwait(false);
            _transaction = null;
        }
    }

    // -------------------------------------------------------------- repositories / sets

    public virtual IRepository<TEntity, TKey> GetRepository<TEntity, TKey>() where TEntity : class, IEntity<TKey> =>
        (IRepository<TEntity, TKey>)ServiceProvider.GetService(typeof(IRepository<TEntity, TKey>))!;

    public IAsyncRepository<TEntity, TKey> GetAsyncRepository<TEntity, TKey>() where TEntity : class, IEntity<TKey> =>
        (IAsyncRepository<TEntity, TKey>)ServiceProvider.GetService(typeof(IAsyncRepository<TEntity, TKey>))!;

    public IQueryableRepository<TEntity, TKey> GetQueryableRepository<TEntity, TKey>() where TEntity : class, IEntity<TKey> =>
        (IQueryableRepository<TEntity, TKey>)ServiceProvider.GetService(typeof(IQueryableRepository<TEntity, TKey>))!;

    public IAsyncQueryableRepository<TEntity, TKey> GetAsyncQueryableRepository<TEntity, TKey>() where TEntity : class, IEntity<TKey> =>
        (IAsyncQueryableRepository<TEntity, TKey>)ServiceProvider.GetService(typeof(IAsyncQueryableRepository<TEntity, TKey>))!;

    public Data.Repository.ISet<TEntity> CreateSet<TEntity>() where TEntity : class, IEntity =>
        new RelationalSet<TEntity>(this);

    // -------------------------------------------------------------- change-tracking hooks (mapped to the lean model)

    /// <summary>Stages the current entity as a full-row update by key.</summary>
    public void ApplyCurrentValues<TEntity>(TEntity original, TEntity current) where TEntity : class, IEntity =>
        StageUpdate(current);

    /// <summary>No-op: the lean model does not track read entities.</summary>
    public void Attach<TEntity>(TEntity item) where TEntity : class, IEntity { }

    /// <summary>Stages the entity as a full-row update by key.</summary>
    public void SetModified<TEntity>(TEntity item) where TEntity : class => StageUpdate(item);

    public void LoadCollection<TEntity, TElement>(TEntity item,
        Expression<Func<TEntity, IEnumerable<TElement>>> navigationProperty,
        Expression<Func<TElement, bool>>? filter = null)
        where TEntity : class where TElement : class =>
        throw new NotSupportedException("The lean relational unit of work does not track navigations; query the related set explicitly.");

    public Task LoadCollectionAsync<TEntity, TElement>(TEntity item,
        Expression<Func<TEntity, IEnumerable<TElement>>> navigationProperty,
        Expression<Func<TElement, bool>>? filter = null)
        where TEntity : class where TElement : class =>
        throw new NotSupportedException("The lean relational unit of work does not track navigations; query the related set explicitly.");

    public void Reload<TEntity>(TEntity item) where TEntity : class =>
        throw new NotSupportedException("The lean unit of work does not track read entities; re-query the row instead.");

    // -------------------------------------------------------------- write rendering

    /// <summary>Renders one staged write into a batch command; true when the command reads back a generated key.</summary>
    private bool Render(PendingWrite write, DbBatchCommand command, List<(PendingWrite Write, object NewVersion)> bumped)
    {
        var configuration = write.Configuration;
        var key = configuration.Key;
        var token = configuration.ConcurrencyToken;
        var parameters = new List<object?>();

        string Bind(object? value)
        {
            parameters.Add(Dialect.BindValue(value));
            return "@p" + (parameters.Count - 1);
        }

        var returning = false;
        switch (write.Kind)
        {
            case PendingWriteKind.Insert:
            {
                var columns = configuration.KeyIsGenerated
                    ? configuration.Columns.Where(column => column != key).ToList()
                    : configuration.Columns.ToList();
                var names = string.Join(", ", columns.Select(column => Dialect.Quote(column.Name)));
                var values = string.Join(", ", columns.Select(column => Bind(column.Read(write.Entity))));
                command.CommandText = Dialect.InsertSql(Dialect.Quote(configuration.TableName), names, values,
                    configuration.KeyIsGenerated ? Dialect.Quote(key.Name) : null);
                returning = configuration.KeyIsGenerated;
                break;
            }

            case PendingWriteKind.Update:
            {
                // The token it read goes into the WHERE; the bumped value goes into the SET (written back on success).
                var columns = configuration.Columns.Where(column => !configuration.Keys.Contains(column) && column != token).ToList();
                var set = string.Join(", ", columns.Select(column =>
                    $"{Dialect.Quote(column.Name)} = {Bind(column.Read(write.Entity))}"));
                var where = string.Empty;
                if (token is not null)
                {
                    var current = token.Property.GetValue(write.Entity);
                    var next = NextVersion(current, token.Property.PropertyType);
                    bumped.Add((write, next));
                    set += $", {Dialect.Quote(token.Name)} = {Bind(next)}";
                    where = $" AND {Dialect.Quote(token.Name)} = {Bind(current)}";
                }

                command.CommandText =
                    $"UPDATE {Dialect.Quote(configuration.TableName)} SET {set} " +
                    $"WHERE {KeyWhere(configuration, write.Entity, Bind)}" + where;
                break;
            }

            case PendingWriteKind.Delete:
            {
                command.CommandText =
                    $"DELETE FROM {Dialect.Quote(configuration.TableName)} " +
                    $"WHERE {KeyWhere(configuration, write.Entity, Bind)}";
                if (token is not null)
                {
                    bumped.Add((write, token.Property.GetValue(write.Entity)!));
                    command.CommandText += $" AND {Dialect.Quote(token.Name)} = {Bind(token.Property.GetValue(write.Entity))}";
                }

                break;
            }
        }

        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "p" + index;
            parameter.Value = parameters[index] ?? DBNull.Value;
            Dialect.ConfigureParameter(parameter, parameters[index]);
            command.Parameters.Add(parameter);
        }

        return returning;
    }

    /// <summary>The WHERE addressing one row by its (possibly composite) key.</summary>
    private string KeyWhere(RelationalEntityConfiguration configuration, object entity, Func<object?, string> bind) =>
        string.Join(" AND ", configuration.Keys.Select(key =>
            $"{Dialect.Quote(key.Name)} = {bind(key.Property.GetValue(entity))}"));

    /// <summary>The next token value: integers increment, Guids renew.</summary>
    private static object NextVersion(object? current, Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(int))
        {
            return ((int?)current ?? 0) + 1;
        }

        if (underlying == typeof(long))
        {
            return ((long?)current ?? 0L) + 1L;
        }

        return Guid.NewGuid();
    }

    private static void AssignGeneratedKey(PendingWrite write, object value)
    {
        var property = write.Configuration.Key.Property;
        var target = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        property.SetValue(write.Entity, target.IsInstanceOfType(value) ? value : Convert.ChangeType(value, target));
    }

    // -------------------------------------------------------------- dispose

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _transaction?.Dispose();
            _connection?.Dispose();
        }

        _disposed = true;
    }

    private enum PendingWriteKind
    {
        Insert,
        Update,
        Delete,
    }

    private sealed record PendingWrite(RelationalEntityConfiguration Configuration, PendingWriteKind Kind, object Entity);
}

/// <summary>The strongly-typed unit of work a consumer derives for its database.</summary>
public abstract class RelationalUnitOfWork<TDatabase>(IServiceProvider serviceProvider, DbDataSource dataSource, SqlDialect dialect, RelationalModel model)
    : RelationalUnitOfWork(serviceProvider, dataSource, dialect, model)
    where TDatabase : class;
