using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using eQuantic.Core.Data.Query;
using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.Relational.Repository;

/// <summary>
///     A native relational entity set. Entity inserts are staged on the <see cref="RelationalUnitOfWork" /> and
///     applied on commit; set-based writes run immediately as single SQL statements with real affected-row
///     counts, scoped by the global query filters. Enumerating the set is a full-table read — prefer the
///     repository with a <c>QueryOptions</c>.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public sealed class RelationalSet<TEntity> : Data.Repository.ISet<TEntity> where TEntity : class, IEntity
{
    private readonly RelationalUnitOfWork _unitOfWork;
    private readonly RelationalEntityConfiguration _configuration;
    private readonly SqlDialect _dialect;

    internal RelationalSet(RelationalUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
        _configuration = unitOfWork.Configuration<TEntity>();
        _dialect = unitOfWork.Dialect;
    }

    // ---------------------------------------------------------------- IQueryable (no LINQ provider — query through the repository)
    public Type ElementType => typeof(TEntity);

    public Expression Expression =>
        throw new NotSupportedException("The native relational set has no LINQ provider; query through the repository with a QueryOptions.");

    public IQueryProvider Provider =>
        throw new NotSupportedException("The native relational set has no LINQ provider; query through the repository with a QueryOptions.");

    public IEnumerator<TEntity> GetEnumerator() => Execute().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ---------------------------------------------------------------- reads
    public IEnumerable<TEntity> Execute() => ExecuteAsync().GetAwaiter().GetResult();

    private async Task<List<TEntity>> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var columns = _configuration.Columns;
        var sql = $"SELECT {string.Join(", ", columns.Select(column => _dialect.Quote(column.Name)))} FROM {_dialect.Quote(_configuration.TableName)}";
        await using var command = await _unitOfWork.CommandAsync(sql, [], cancellationToken).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var entities = new List<TEntity>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entities.Add(RelationalMaterializer.Materialize<TEntity>(reader, columns));
        }

        return entities;
    }

    public TEntity? Find<TKey>(TKey key) => FindAsync(key).GetAwaiter().GetResult();

    public async Task<TEntity?> FindAsync<TKey>(TKey key, CancellationToken cancellationToken = default)
    {
        // A composite key arrives as a tuple: its items align with the declared key columns, in order.
        object?[] values = _configuration.HasCompositeKey && key is System.Runtime.CompilerServices.ITuple tuple
            ? Enumerable.Range(0, tuple.Length).Select(index => tuple[index]).ToArray()
            : [key];
        if (values.Length != _configuration.Keys.Count)
        {
            throw new ArgumentException(
                $"'{typeof(TEntity).Name}' has a key of {_configuration.Keys.Count} column(s); pass one value per key column.",
                nameof(key));
        }

        var columns = _configuration.Columns;
        var where = string.Join(" AND ",
            _configuration.Keys.Select((column, index) => $"{_dialect.Quote(column.Name)} = @p{index}"));
        var sql = $"SELECT {string.Join(", ", columns.Select(column => _dialect.Quote(column.Name)))}"
                  + $" FROM {_dialect.Quote(_configuration.TableName)} WHERE {where}";
        await using var command = await _unitOfWork.CommandAsync(sql, values, cancellationToken).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? RelationalMaterializer.Materialize<TEntity>(reader, columns)
            : null;
    }

    // ---------------------------------------------------------------- entity writes (staged → commit)
    public void Insert(TEntity item) => _unitOfWork.StageInsert(item);

    public Task InsertAsync(TEntity item, CancellationToken cancellationToken = default)
    {
        _unitOfWork.StageInsert(item);
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- set-based writes (immediate)
    public long DeleteMany(Expression<Func<TEntity, bool>> filter) => DeleteManyAsync(filter).GetAwaiter().GetResult();

    public async Task<long> DeleteManyAsync(Expression<Func<TEntity, bool>> filter, CancellationToken cancellationToken = default)
    {
        // A soft-delete entity's set-based delete stamps DeletedAt instead — the rows survive, scoped out of reads.
        if (EntityLifecycle.IsSoftDelete(typeof(TEntity), _unitOfWork.Conventions))
        {
            return await UpdateManyAsync(filter, EntityLifecycle.SoftDeleteUpdate<TEntity>(_unitOfWork.Conventions, _unitOfWork.Services), cancellationToken).ConfigureAwait(false);
        }

        if (ReadsOwnTable(filter))
        {
            return await ByLockedKeysAsync(filter, _ => $"DELETE FROM {Table}", cancellationToken).ConfigureAwait(false);
        }

        // The global filter scopes set-based writes too (a tenant-scoped delete stays tenant-scoped).
        var (where, parameters) = RelationalSql.Where(_dialect, _configuration, filter, _unitOfWork.GlobalFilter<TEntity>(),
            _unitOfWork.Relations);
        var sql = $"DELETE FROM {_dialect.Quote(_configuration.TableName)} WHERE {where}";
        await using var command = await _unitOfWork.CommandAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public long UpdateMany(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateExpression) =>
        UpdateManyAsync(filter, updateExpression).GetAwaiter().GetResult();

    /// <remarks>
    ///     One <c>UPDATE … WHERE</c>: the store applies it to each row only while that row still matches (a row another
    ///     writer changed first is evaluated again), so a filter on the key and other members is a compare-and-set,
    ///     and the count is the rows it matched, changed or not.
    /// </remarks>
    public async Task<long> UpdateManyAsync(Expression<Func<TEntity, bool>> filter,
        Expression<Func<TEntity, TEntity>> updateExpression, CancellationToken cancellationToken = default)
    {
        if (ReadsOwnTable(filter))
        {
            return await _unitOfWork.TranslatingAsync(() => ByLockedKeysAsync(filter,
                setParameters => $"UPDATE {Table} SET {SetList(updateExpression, setParameters, existingRow: null)}", cancellationToken))
                .ConfigureAwait(false);
        }

        var parameters = new List<object?>();
        var set = SetList(updateExpression, parameters, existingRow: null);
        var where = RelationalSql.Where(_dialect, _configuration, filter, _unitOfWork.GlobalFilter<TEntity>(), parameters,
            _unitOfWork.Relations);

        var sql = $"UPDATE {Table} SET {set} WHERE {where}";
        return await _unitOfWork.TranslatingAsync(async () =>
        {
            await using var command = await _unitOfWork.CommandAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
            return (long)await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- returning updates (immediate)

    /// <summary>
    ///     Applies the update as <see cref="UpdateManyAsync" /> does and returns the rows it wrote, as stored after it:
    ///     one <c>UPDATE … RETURNING</c> / <c>OUTPUT INSERTED</c> where the dialect has it; otherwise the matching rows
    ///     are locked (<c>SELECT … FOR UPDATE</c>), updated and read back in one transaction.
    /// </summary>
    internal async Task<IReadOnlyList<TEntity>> UpdateAndGetAsync(Expression<Func<TEntity, bool>> filter,
        Expression<Func<TEntity, TEntity>> updateExpression, CancellationToken cancellationToken)
    {
        var columns = _configuration.Columns;
        var parameters = new List<object?>();
        var set = SetList(updateExpression, parameters, existingRow: null);
        var where = RelationalSql.Where(_dialect, _configuration, filter, _unitOfWork.GlobalFilter<TEntity>(), parameters,
            _unitOfWork.Relations);

        if (_dialect.UpdateReturningSql(Table, set, where, columns.Select(column => _dialect.Quote(column.Name)).ToList()) is { } returning)
        {
            return await _unitOfWork.TranslatingAsync(() => ReadAsync(returning, parameters, cancellationToken)).ConfigureAwait(false);
        }

        return await _unitOfWork.TranslatingAsync(() => _unitOfWork.AtomicallyAsync<IReadOnlyList<TEntity>>(async () =>
        {
            // The rows the filter matches, locked until the transaction ends: nobody changes them between the
            // update and the read that hands them back.
            var keyColumns = string.Join(", ", _configuration.Keys.Select(key => _dialect.Quote(key.Name)));
            var whereParameters = new List<object?>();
            var lockedWhere = RelationalSql.Where(_dialect, _configuration, filter, _unitOfWork.GlobalFilter<TEntity>(), whereParameters,
                _unitOfWork.Relations);
            var keys = new List<object?[]>();
            await using (var command = await _unitOfWork.CommandAsync(
                             $"SELECT {keyColumns} FROM {Table} WHERE {lockedWhere}{_dialect.ForUpdateClause}", whereParameters,
                             cancellationToken).ConfigureAwait(false))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var key = new object?[_configuration.Keys.Count];
                    reader.GetValues(key!);
                    keys.Add(key);
                }
            }

            if (keys.Count == 0)
            {
                return [];
            }

            foreach (var (update, updateParameters) in ByKeys(keys, filter,
                         setParameters => $"UPDATE {Table} SET {SetList(updateExpression, setParameters, existingRow: null)}"))
            {
                await ExecuteAsync(update, updateParameters, cancellationToken).ConfigureAwait(false);
            }

            var rows = new List<TEntity>(keys.Count);
            foreach (var (read, readParameters) in ByKeys(keys, null, _ => $"SELECT {ColumnList} FROM {Table}"))
            {
                rows.AddRange(await ReadAsync(read, readParameters, cancellationToken).ConfigureAwait(false));
            }

            return rows;
        }, cancellationToken)).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- upserts (immediate)

    /// <summary>
    ///     Inserts the entity, or updates the row matching it on the target members, atomically, and returns the row as
    ///     stored: one statement where the dialect has one (<c>INSERT … ON CONFLICT</c>, <c>MERGE … WITH (HOLDLOCK)</c>);
    ///     otherwise, under the dialect's named lock on the target where it takes one
    ///     (<see cref="SqlDialect.LocksUpsertTargets" />), a plain read of the target, then the update of the row it found,
    ///     locked, or the insert — and, when a concurrent insert of the target wins, the update of that row (run again only
    ///     after a deadlock outside an explicit transaction). On a key the store generates, the row holding the entity's key
    ///     is updated, and a key no row holds inserts a row the store numbers.
    /// </summary>
    internal async Task<TEntity> UpsertAsync(TEntity entity, IReadOnlyList<string>? on,
        Expression<Func<TEntity, TEntity>>? update, Expression<Func<TEntity, bool>>? conflictFilter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var target = on is null
            ? _configuration.Keys
            : on.Select(member => _configuration.ColumnFor(member)
                                  ?? throw new NotSupportedException($"'{typeof(TEntity).Name}' has no mapped member '{member}' to upsert on."))
                .ToList();

        // The insert's stamps go on the entity; the update's are assignments of their own, so each path writes its own.
        EntityLifecycle.StampForInsert(entity, _unitOfWork.Conventions, _unitOfWork.Services);

        return _dialect.UpsertsInOneStatement
            ? await _unitOfWork.TranslatingAsync(() => UpsertInOneStatementAsync(entity, target, update, conflictFilter, cancellationToken)).ConfigureAwait(false)
            : await UpsertWithLockingReadAsync(entity, target, update, conflictFilter, cancellationToken).ConfigureAwait(false);
    }

    private Task<TEntity> UpsertInOneStatementAsync(TEntity entity, IReadOnlyList<RelationalColumn> target,
        Expression<Func<TEntity, TEntity>>? update, Expression<Func<TEntity, bool>>? conflictFilter,
        CancellationToken cancellationToken)
    {
        var parameters = new List<object?>();
        var insertColumns = InsertColumns();
        var insertValues = insertColumns.Select(column => Bind(parameters, column.Read(entity))).ToList();
        var targetValues = target.Select(column => insertColumns.Contains(column)
            ? insertValues[insertColumns.IndexOf(column)]
            : Bind(parameters, column.Read(entity))).ToList();

        // A generated key the upsert targets is written by the insert only where the statement finds the target by what
        // the insert writes (PostgreSQL's ON CONFLICT): the entity's key when a row holds it, the store's number otherwise.
        var keyAt = target.ToList().IndexOf(_configuration.Key);
        if (_configuration.KeyIsGenerated && keyAt >= 0
            && _dialect.GeneratedKeyUpsertValue(Table, _configuration.Key, targetValues[keyAt]) is { } keyValue)
        {
            insertColumns.Add(_configuration.Key);
            insertValues.Add(keyValue);
        }

        var existingRow = _dialect.UpsertExistingRow(Table);
        var assignments = UpdateAssignments(entity, target, update, parameters, existingRow,
            column => insertColumns.Contains(column) ? insertValues[insertColumns.IndexOf(column)] : Bind(parameters, column.Read(entity)));
        if (assignments.Count == 0)
        {
            // Nothing to write beyond what identifies the row: the update still happens, so the row comes back.
            var first = _dialect.Quote(target[0].Name);
            assignments.Add($"{first} = {existingRow}.{first}");
        }

        // The conflict filter names a partial unique index's predicate, which PostgreSQL matches when it plans the
        // statement: written with its values in place, it still matches once the driver has prepared the statement and the
        // store plans it once for any value — bound as parameters, it matched nothing after a few executions.
        var filter = conflictFilter is null
            ? null
            : SqlFilterRenderer.RenderInlined(_dialect, _configuration, FilterInterpreter.Interpret(conflictFilter));

        var sql = _dialect.UpsertSql(Table, insertColumns.Select(column => _dialect.Quote(column.Name)).ToList(), insertValues,
            target.Select(column => _dialect.Quote(column.Name)).ToList(), targetValues, filter, assignments,
            _configuration.Columns.Select(column => _dialect.Quote(column.Name)).ToList());
        return SingleAsync(sql, parameters, cancellationToken);
    }

    private async Task<TEntity> UpsertWithLockingReadAsync(TEntity entity, IReadOnlyList<RelationalColumn> target,
        Expression<Func<TEntity, TEntity>>? update, Expression<Func<TEntity, bool>>? conflictFilter,
        CancellationToken cancellationToken)
    {
        if (_configuration.KeyIsGenerated && target.SequenceEqual(_configuration.Keys) && !ReturnsInsertedRows)
        {
            throw new NotSupportedException(
                $"{_dialect.System} cannot hand back a key it generates, and an upsert on one inserts a row the store numbers " +
                "when no row holds the entity's key; upsert on a unique member instead, or give the entity a client-assigned key.");
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _unitOfWork.AtomicallyAsync(
                    () => UpsertOnceAsync(entity, target, update, conflictFilter, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < UpsertAttempts && _dialect.IsDeadlock(exception)
                                              && !_unitOfWork.InExplicitTransaction)
            {
                // The store chose this upsert as a deadlock's victim and rolled its own transaction back: it runs again.
                // (Another upsert of its target waits on the target's lock instead, where the dialect takes one: this is
                // a writer that is not one.) Inside an explicit transaction the caller's transaction is what the store
                // rolled back, which no retry can resume.
                await Task.Delay(Random.Shared.Next(5, 20 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not DuplicateKeyException && _dialect.DuplicateKey(exception) is { } duplicate)
            {
                throw duplicate;
            }
        }
    }

    /// <summary>
    ///     One attempt of an upsert by reading, in a transaction (the caller's, or one of its own). Where the dialect
    ///     takes one, the target's named lock comes first (<see cref="LockTargetAsync" />), so no other upsert of the
    ///     target runs until this transaction ends. The target is read without a row lock first: a locking read of a row
    ///     that is not there locks the gap where it would go, and two such gap locks (two upserts of different targets
    ///     that fall in one gap) each block the other's insert — the deadlock InnoDB answers by rolling a transaction
    ///     back, which inside an explicit transaction is the caller's. A row the plain read finds is read again under a
    ///     lock and updated. A row it does not find is inserted; when another insert of the target won (one this
    ///     transaction's snapshot predates, or a writer that is not an upsert), the store refuses this one with a
    ///     duplicate key, which rolls back the statement alone, and the row that won is read under a lock and updated.
    ///     <para>
    ///         Without the named lock, three or more upserts of a target that is not there yet deadlock: the inserts
    ///         that lose each hold a shared lock on the winner's row and then ask for an exclusive one.
    ///     </para>
    /// </summary>
    private async Task<TEntity> UpsertOnceAsync(TEntity entity, IReadOnlyList<RelationalColumn> target,
        Expression<Func<TEntity, TEntity>>? update, Expression<Func<TEntity, bool>>? conflictFilter,
        CancellationToken cancellationToken)
    {
        await LockTargetAsync(entity, target, cancellationToken).ConfigureAwait(false);

        var row = await ReadTargetAsync(entity, target, conflictFilter, locking: false, cancellationToken).ConfigureAwait(false) is null
            ? null
            : await ReadTargetAsync(entity, target, conflictFilter, locking: true, cancellationToken).ConfigureAwait(false);

        if (row is null)
        {
            DuplicateKeyException taken;
            try
            {
                return await InsertTargetAsync(entity, target, conflictFilter, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (_dialect.DuplicateKey(exception) is { } duplicate)
            {
                taken = duplicate;
            }

            row = await ReadTargetAsync(entity, target, conflictFilter, locking: true, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                // The insert took a key or a unique key that is not the target: no update can take its place.
                throw taken;
            }
        }

        var updateParameters = new List<object?>();
        var assignments = UpdateAssignments(entity, target, update, updateParameters, existingRow: null,
            column => Bind(updateParameters, column.Read(entity)));
        if (assignments.Count == 0)
        {
            // Nothing to write: the row read under the lock is the row as stored.
            return row;
        }

        var byKey = TargetWhere(row, _configuration.Keys, null, updateParameters);
        await ExecuteAsync($"UPDATE {Table} SET {string.Join(", ", assignments)} WHERE {byKey}", updateParameters,
            cancellationToken).ConfigureAwait(false);

        var storedParameters = new List<object?>();
        var stored = await ReadAsync(
            $"SELECT {ColumnList} FROM {Table} WHERE {TargetWhere(row, _configuration.Keys, null, storedParameters)}",
            storedParameters, cancellationToken).ConfigureAwait(false);
        return stored[0];
    }

    /// <summary>
    ///     Takes the upsert target's named lock for the transaction the upsert runs in (the caller's explicit one, or its
    ///     own), when the dialect <see cref="SqlDialect.LocksUpsertTargets" />. Upserts of one target then run one after
    ///     the other, from before their first read to the end of their transactions; upserts of different targets take
    ///     different locks, as far as the store's text keys tell them apart. A target the store numbers (a generated
    ///     key) takes none: its insert never meets another's.
    ///     <para>
    ///         The name is the table's and the target's values': a text value by the key the store answers for it
    ///         (<see cref="SqlDialect.UpsertTargetKeysSql" />: the same for every two values its column compares equal),
    ///         any other value in one invariant form per kind (an <c>int</c> and a <c>long</c> of one number, or
    ///         <c>1.50m</c> and <c>1.5m</c>, are one value). The wait is the one the store answers with the keys; a lock
    ///         still held after it fails the upsert with <see cref="TimeoutException" /> before anything is written.
    ///     </para>
    /// </summary>
    private async Task LockTargetAsync(TEntity entity, IReadOnlyList<RelationalColumn> target, CancellationToken cancellationToken)
    {
        if (!_dialect.LocksUpsertTargets || (_configuration.KeyIsGenerated && target.SequenceEqual(_configuration.Keys)))
        {
            return;
        }

        var values = target.Select(column => column.Read(entity)).ToList();
        var texts = Enumerable.Range(0, target.Count).Where(index => values[index] is string or char).ToList();
        var parameters = new List<object?>();
        var markers = texts.Select(index => Bind(parameters, values[index] is char character ? character.ToString() : values[index])).ToList();
        var sql = _dialect.UpsertTargetKeysSql(Table, texts.Select(index => _dialect.Quote(target[index].Name)).ToList(), markers);

        object?[] answer;
        await using (var command = await _unitOfWork.CommandAsync(sql, parameters, cancellationToken).ConfigureAwait(false))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.FieldCount != texts.Count + 1)
            {
                throw new InvalidOperationException(
                    $"{_dialect.System} answered no wait and keys for the upsert target's lock of '{typeof(TEntity).Name}'.");
            }

            answer = new object?[reader.FieldCount];
            reader.GetValues(answer!);
        }

        var wait = answer[0] is null or DBNull
            ? Timeout.InfiniteTimeSpan
            : TimeSpan.FromSeconds(Convert.ToDouble(answer[0], CultureInfo.InvariantCulture));
        var name = new StringBuilder("eQuantic.Core.Data/upsert");
        AppendLockPart(name, 't', _configuration.TableName);
        for (var index = 0; index < target.Count; index++)
        {
            var text = texts.IndexOf(index);
            if (text < 0)
            {
                AppendLockValue(name, _dialect.BindValue(values[index]));
            }
            else if (answer[text + 1] is { } key and not DBNull)
            {
                AppendLockPart(name, 's', key is byte[] bytes
                    ? Convert.ToHexString(bytes)
                    : Convert.ToString(key, CultureInfo.InvariantCulture)!);
            }
            else
            {
                throw new InvalidOperationException(
                    $"{_dialect.System} answered no key for the value of '{target[index].Property.Name}' in the upsert target's lock.");
            }
        }

        try
        {
            await _unitOfWork.LockForTransactionAsync(name.ToString(), wait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException timedOut)
        {
            throw new TimeoutException(
                $"The upsert of '{typeof(TEntity).Name}' waited {wait.TotalSeconds:0.###} s, the store's wait for a row lock, for " +
                "another upsert of the same target to end its transaction; nothing was written.", timedOut);
        }
    }

    /// <summary>One part of a target lock's name: its kind, its length and its text, so no two lists of parts read alike.</summary>
    private static void AppendLockPart(StringBuilder name, char kind, string text) =>
        name.Append('/').Append(kind).Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text);

    /// <summary>A value of an upsert's target that is not text, as bound, in one invariant form per kind of value.</summary>
    private static void AppendLockValue(StringBuilder name, object? value)
    {
        switch (value)
        {
            case null:
                name.Append("/z");
                return;
            case bool flag:
                AppendLockPart(name, 'n', flag ? "1" : "0");
                return;
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                AppendLockPart(name, 'n', Convert.ToString(value, CultureInfo.InvariantCulture)!);
                return;
            case decimal number:
                AppendLockPart(name, 'n', DecimalText(number));
                return;
            case float or double:
                AppendLockPart(name, 'n', ((IFormattable)value).ToString("R", CultureInfo.InvariantCulture));
                return;
            case Guid guid:
                AppendLockPart(name, 'g', guid.ToString("D"));
                return;
            case DateTime moment:
                AppendLockPart(name, 'd', moment.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
                return;
            case DateTimeOffset moment:
                AppendLockPart(name, 'd', moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
                return;
            case byte[] bytes:
                AppendLockPart(name, 'b', Convert.ToHexString(bytes));
                return;
            case string text:
                // Text the dialect bound for a value that is not text (an enum's label): one value, one text.
                AppendLockPart(name, 'e', text);
                return;
            default:
                AppendLockPart(name, 'x', value.GetType().FullName + ":" + Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
        }
    }

    /// <summary>A decimal without trailing zeros (<c>1.50</c> and <c>1.5</c> are one number).</summary>
    private static string DecimalText(decimal number)
    {
        var text = number.ToString(CultureInfo.InvariantCulture);
        if (text.Contains('.', StringComparison.Ordinal))
        {
            text = text.TrimEnd('0').TrimEnd('.');
        }

        return text == "-0" ? "0" : text;
    }

    /// <summary>The row the upsert targets, read plainly or under a lock that holds it until the transaction ends.</summary>
    private async Task<TEntity?> ReadTargetAsync(TEntity entity, IReadOnlyList<RelationalColumn> target,
        Expression<Func<TEntity, bool>>? conflictFilter, bool locking, CancellationToken cancellationToken)
    {
        var parameters = new List<object?>();
        var where = TargetWhere(entity, target, conflictFilter, parameters);
        var rows = await ReadAsync($"SELECT {ColumnList} FROM {Table} WHERE {where}{(locking ? _dialect.ForUpdateClause : string.Empty)}",
            parameters, cancellationToken).ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>
    ///     Inserts the upsert's entity and returns the row as stored: with <c>RETURNING</c> where the dialect has it
    ///     (MariaDB, which hands back a key it generates too), otherwise read back by its key — or by the target, when the
    ///     store generated the key.
    /// </summary>
    private async Task<TEntity> InsertTargetAsync(TEntity entity, IReadOnlyList<RelationalColumn> target,
        Expression<Func<TEntity, bool>>? conflictFilter, CancellationToken cancellationToken)
    {
        var parameters = new List<object?>();
        var columns = InsertColumns();
        var columnList = string.Join(", ", columns.Select(column => _dialect.Quote(column.Name)));
        var values = string.Join(", ", columns.Select(column => Bind(parameters, column.Read(entity))));
        if (ReturnsInsertedRows)
        {
            return await SingleAsync(_dialect.InsertSql(Table, columnList, values,
                _configuration.Columns.Select(column => _dialect.Quote(column.Name)).ToList()), parameters, cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(_dialect.InsertSql(Table, columnList, values, (string?)null), parameters, cancellationToken).ConfigureAwait(false);

        var insertedParameters = new List<object?>();
        var insertedWhere = _configuration.KeyIsGenerated
            ? TargetWhere(entity, target, conflictFilter, insertedParameters)
            : TargetWhere(entity, _configuration.Keys, null, insertedParameters);
        var inserted = await ReadAsync($"SELECT {ColumnList} FROM {Table} WHERE {insertedWhere}",
            insertedParameters, cancellationToken).ConfigureAwait(false);
        return inserted[0];
    }

    /// <summary>Whether an insert can return the row it wrote (<c>RETURNING</c>), a key the store generated included.</summary>
    private bool ReturnsInsertedRows => _dialect.ReadsBackInsertedValues && !_dialect.ReadsBackByKey;

    private const int UpsertAttempts = 25;

    /// <summary>
    ///     The update's assignments: the update expression's (reading the existing row where it reads the row) or else
    ///     every mapped column but the key, the target, the store-generated columns and the creation stamps; then the
    ///     update's lifecycle stamps unless the expression assigns them, and the concurrency token's bump.
    /// </summary>
    private List<string> UpdateAssignments(TEntity entity, IReadOnlyList<RelationalColumn> target,
        Expression<Func<TEntity, TEntity>>? update, List<object?> parameters, string? existingRow,
        Func<RelationalColumn, string> valueOf)
    {
        var token = _configuration.ConcurrencyToken;
        var creation = EntityLifecycle.CreationMembers(typeof(TEntity), _unitOfWork.Conventions);
        var assignments = new List<string>();
        var assigned = new HashSet<RelationalColumn>();

        if (update is not null)
        {
            foreach (var assignment in UpdateInterpreter.Interpret(update))
            {
                var column = _configuration.ColumnFor(assignment.Name)!;
                if (_configuration.Keys.Contains(column) || column.IsStoreGenerated || column == token)
                {
                    throw new NotSupportedException(
                        $"An upsert's update cannot assign '{assignment.Name}': the key identifies the row, the store " +
                        "assigns a store-generated member, and the engine bumps the concurrency token.");
                }

                assigned.Add(column);
            }

            assignments.AddRange(SqlUpdateRenderer.Fragments(_dialect, _configuration, update, parameters, existingRow));
        }
        else
        {
            foreach (var column in _configuration.Columns)
            {
                if (_configuration.Keys.Contains(column) || target.Contains(column) || column.IsStoreGenerated || column == token
                    || creation.Contains(column.Property.Name))
                {
                    continue;
                }

                assigned.Add(column);
                assignments.Add($"{_dialect.Quote(column.Name)} = {valueOf(column)}");
            }
        }

        // The update's stamps replace whatever the entity carried for them (the default update above wrote those
        // columns from the entity: the stamp's assignment takes their place).
        foreach (var stamp in EntityLifecycle.UpdateStamps(typeof(TEntity), _unitOfWork.Conventions, _unitOfWork.Services))
        {
            if (_configuration.ColumnFor(stamp.Name) is not { } column
                || (update is not null && assigned.Contains(column)))
            {
                continue;
            }

            var quoted = _dialect.Quote(column.Name);
            assignments.RemoveAll(fragment => fragment.StartsWith(quoted + " =", StringComparison.Ordinal));
            assignments.Add($"{quoted} = {Bind(parameters, column.Store(stamp.Value))}");
        }

        if (token is not null)
        {
            var quoted = _dialect.Quote(token.Name);
            var current = existingRow is null ? quoted : existingRow + "." + quoted;
            var type = Nullable.GetUnderlyingType(token.Property.PropertyType) ?? token.Property.PropertyType;
            // An unset version is followed by 1, as the unit of work's next version is: NULL + 1 alone is NULL.
            var number = Nullable.GetUnderlyingType(token.Property.PropertyType) is null ? current : $"COALESCE({current}, 0)";
            assignments.Add(type == typeof(int) || type == typeof(long)
                ? $"{quoted} = {number} + 1"
                : $"{quoted} = {Bind(parameters, RelationalUnitOfWork.NextVersion(null, type))}");
        }

        return assignments;
    }

    // ---------------------------------------------------------------- inserts skipping duplicates (immediate)

    /// <summary>
    ///     Inserts the batch at once and counts the rows inserted. With <paramref name="skipDuplicates" />, the
    ///     dialect's statement leaves out the rows whose key or unique key is taken (<c>ON CONFLICT DO NOTHING</c>,
    ///     <c>TRY…CATCH</c> per row), or the rows go one by one and only a duplicate-key error is discarded; without
    ///     it, a taken key fails the call — and, the batch being one transaction, nothing of it is written.
    /// </summary>
    internal async Task<long> InsertManyAsync(IEnumerable<TEntity> items, bool skipDuplicates, CancellationToken cancellationToken)
    {
        var entities = items as IReadOnlyList<TEntity> ?? items.ToList();
        if (entities.Count == 0)
        {
            return 0;
        }

        foreach (var entity in entities)
        {
            EntityLifecycle.StampForInsert(entity, _unitOfWork.Conventions, _unitOfWork.Services);
        }

        var columns = InsertColumns();
        var columnList = string.Join(", ", columns.Select(column => _dialect.Quote(column.Name)));
        var rowsPerStatement = Math.Max(1, Math.Min(_dialect.MaxRowsPerInsert, _dialect.MaxParameters / Math.Max(1, columns.Count)));

        return await _unitOfWork.TranslatingAsync(() => _unitOfWork.AtomicallyAsync(async () =>
        {
            long inserted = 0;
            foreach (var chunk in entities.Chunk(rowsPerStatement))
            {
                var parameters = new List<object?>();
                var rows = chunk.Select(entity => "(" + string.Join(", ", columns.Select(column => Bind(parameters, column.Read(entity)))) + ")")
                    .ToList();
                if (!skipDuplicates)
                {
                    inserted += await ExecuteAsync($"INSERT INTO {Table} ({columnList}) VALUES {string.Join(", ", rows)}", parameters,
                        cancellationToken).ConfigureAwait(false);
                }
                else if (_dialect.InsertSkippingDuplicatesSql(Table, columnList, rows) is { } skipping)
                {
                    await using var command = await _unitOfWork.CommandAsync(skipping, parameters, cancellationToken).ConfigureAwait(false);
                    inserted += Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
                }
                else
                {
                    inserted += await InsertEachDiscardingDuplicatesAsync(chunk, columns, columnList, cancellationToken).ConfigureAwait(false);
                }
            }

            return inserted;
        }, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Inserts row by row, discarding only duplicate-key errors: each insert is its own statement, and a statement
    ///     the store refuses for a taken key rolls back alone — the transaction and the rows before it carry on.
    /// </summary>
    private async Task<long> InsertEachDiscardingDuplicatesAsync(IEnumerable<TEntity> entities, List<RelationalColumn> columns,
        string columnList, CancellationToken cancellationToken)
    {
        long inserted = 0;
        foreach (var entity in entities)
        {
            var parameters = new List<object?>();
            var values = string.Join(", ", columns.Select(column => Bind(parameters, column.Read(entity))));
            try
            {
                inserted += await ExecuteAsync($"INSERT INTO {Table} ({columnList}) VALUES ({values})", parameters,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (_dialect.DuplicateKey(exception) is not null)
            {
                // The row's key or unique key is taken: it is left out.
            }
        }

        return inserted;
    }

    // ---------------------------------------------------------------- helpers

    private string Table => _dialect.Quote(_configuration.TableName);

    private string ColumnList => string.Join(", ", _configuration.Columns.Select(column => _dialect.Quote(column.Name)));

    /// <summary>The columns an insert writes: all but a generated key and the store-generated ones, which the store assigns.</summary>
    private List<RelationalColumn> InsertColumns() =>
        _configuration.Columns
            .Where(column => !(_configuration.KeyIsGenerated && column == _configuration.Key) && !column.IsStoreGenerated)
            .ToList();

    /// <summary>An update's <c>SET</c> list, with the <c>UpdatedAt</c> stamp unless the update assigns it.</summary>
    private string SetList(Expression<Func<TEntity, TEntity>> updateExpression, List<object?> parameters, string? existingRow)
    {
        var fragments = SqlUpdateRenderer.Fragments(_dialect, _configuration, updateExpression, parameters, existingRow);

        // A time-tracked entity's set-based update stamps UpdatedAt unless the caller assigned it explicitly.
        if (EntityLifecycle.UpdateStamp(typeof(TEntity), _unitOfWork.Conventions) is { } stamp
            && _configuration.ColumnFor(stamp.Name) is { } stampColumn
            && !fragments.Any(fragment => fragment.StartsWith(_dialect.Quote(stampColumn.Name) + " =", StringComparison.Ordinal)))
        {
            fragments.Add($"{_dialect.Quote(stampColumn.Name)} = {Bind(parameters, stampColumn.Store(stamp.Value))}");
        }

        return string.Join(", ", fragments);
    }

    /// <summary>The predicate matching an entity's values on the given columns (and the conflict filter).</summary>
    private string TargetWhere(TEntity entity, IReadOnlyList<RelationalColumn> columns,
        Expression<Func<TEntity, bool>>? conflictFilter, List<object?> parameters)
    {
        var clauses = columns.Select(column => column.Read(entity) is { } value
            ? $"{_dialect.Quote(column.Name)} = {Bind(parameters, value)}"
            : $"{_dialect.Quote(column.Name)} IS NULL").ToList();
        if (conflictFilter is not null)
        {
            clauses.Add(SqlFilterRenderer.Render(_dialect, _configuration, FilterInterpreter.Interpret(conflictFilter), parameters));
        }

        return string.Join(" AND ", clauses);
    }

    /// <summary>
    ///     Whether this write's filter crosses a relation back to the written table on a dialect whose <c>UPDATE</c>/<c>DELETE</c>
    ///     cannot read its own table (MySQL and MariaDB, error 1093): then the matching keys are read first.
    /// </summary>
    private bool ReadsOwnTable(Expression<Func<TEntity, bool>> filter) =>
        !_dialect.WritesReadTheirOwnTable
        && _unitOfWork.Relations.ReadsTable(RelationalRelations.And(filter, _unitOfWork.GlobalFilter<TEntity>()), typeof(TEntity));

    /// <summary>
    ///     A set-based write by locked keys, in one transaction (the open one, or its own): the keys of the rows the whole
    ///     filter matches are read with <c>FOR UPDATE</c> — the relation evaluated by a <c>SELECT</c>, which may read its own
    ///     table — so the set cannot change before the write; then the statement writes by those keys, the rest of the filter
    ///     still applied. The count is the rows the write found.
    /// </summary>
    /// <param name="filter">The write's filter.</param>
    /// <param name="statement">The statement before its <c>WHERE</c>, binding its own values first.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    private Task<long> ByLockedKeysAsync(Expression<Func<TEntity, bool>> filter, Func<List<object?>, string> statement,
        CancellationToken cancellationToken) =>
        _unitOfWork.AtomicallyAsync(async () =>
        {
            var keys = await LockedKeysAsync(filter, cancellationToken).ConfigureAwait(false);
            long written = 0;
            foreach (var (sql, parameters) in ByKeys(keys, filter, statement))
            {
                written += await ExecuteAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
            }

            return written;
        }, cancellationToken);

    /// <summary>The keys of the rows the filter (and the global filter) matches, locked until the transaction ends.</summary>
    private async Task<List<object?[]>> LockedKeysAsync(Expression<Func<TEntity, bool>> filter, CancellationToken cancellationToken)
    {
        var keyColumns = string.Join(", ", _configuration.Keys.Select(key => _dialect.Quote(key.Name)));
        var parameters = new List<object?>();
        var where = RelationalSql.Where(_dialect, _configuration, filter, _unitOfWork.GlobalFilter<TEntity>(), parameters, _unitOfWork.Relations);
        var keys = new List<object?[]>();
        await using var command = await _unitOfWork.CommandAsync(
            $"SELECT {keyColumns} FROM {Table} WHERE {where}{_dialect.ForUpdateClause}", parameters, cancellationToken).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = new object?[_configuration.Keys.Count];
            reader.GetValues(key!);
            keys.Add(key);
        }

        return keys;
    }

    /// <summary>
    ///     The statements of a write (or a read) by keys, each binding at most the dialect's parameter limit: the
    ///     statement's own values, rendered once and bound in each, then a share of the keys, then the filter's conjuncts
    ///     that do not read the written table (the ones that do were evaluated by the locking read, and the store would
    ///     refuse them in the write).
    /// </summary>
    /// <param name="keys">The keys of the rows, read from them.</param>
    /// <param name="filter">The write's filter, or <c>null</c> for the keys alone.</param>
    /// <param name="statement">The statement before its <c>WHERE</c>, binding its own values first.</param>
    private IEnumerable<(string Sql, List<object?> Parameters)> ByKeys(IReadOnlyList<object?[]> keys,
        Expression<Func<TEntity, bool>>? filter, Func<List<object?>, string> statement)
    {
        var head = new List<object?>();
        var sql = statement(head);
        var probe = new List<object?>(head);
        RestOf(filter, probe);
        var perStatement = Math.Max(1, (_dialect.MaxParameters - probe.Count) / _configuration.Keys.Count);

        foreach (var chunk in keys.Chunk(perStatement))
        {
            var parameters = new List<object?>(head);
            var where = KeysWhere(chunk, parameters);
            if (RestOf(filter, parameters) is { } rest)
            {
                where += " AND " + rest;
            }

            yield return ($"{sql} WHERE {where}", parameters);
        }
    }

    /// <summary>The filter's conjuncts that do not read the written table, rendered; <c>null</c> when none is left.</summary>
    private string? RestOf(Expression<Func<TEntity, bool>>? filter, List<object?> parameters)
    {
        if (filter is null)
        {
            return null;
        }

        var relations = _unitOfWork.Relations;
        return relations.Without(filter, _unitOfWork.GlobalFilter<TEntity>(), typeof(TEntity)) is Expression<Func<TEntity, bool>> rest
            ? RelationalSql.Where(_dialect, _configuration, rest, null, parameters, relations)
            : null;
    }

    /// <summary>The predicate matching rows by the key values read from them.</summary>
    private string KeysWhere(IReadOnlyList<object?[]> keys, List<object?> parameters)
    {
        if (_configuration.Keys.Count == 1)
        {
            return $"{_dialect.Quote(_configuration.Key.Name)} IN ({string.Join(", ", keys.Select(key => Bind(parameters, key[0])))})";
        }

        return "(" + string.Join(" OR ", keys.Select(key => "(" + string.Join(" AND ",
            _configuration.Keys.Select((column, index) => $"{_dialect.Quote(column.Name)} = {Bind(parameters, key[index])}")) + ")")) + ")";
    }

    private string Bind(List<object?> parameters, object? value)
    {
        parameters.Add(_dialect.BindValue(value));
        return "@p" + (parameters.Count - 1);
    }

    private async Task<IReadOnlyList<TEntity>> ReadAsync(string sql, List<object?> parameters, CancellationToken cancellationToken)
    {
        await using var command = await _unitOfWork.CommandAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<TEntity>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(RelationalMaterializer.Materialize<TEntity>(reader, _configuration.Columns));
        }

        return rows;
    }

    private async Task<TEntity> SingleAsync(string sql, List<object?> parameters, CancellationToken cancellationToken)
    {
        var rows = await ReadAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
        return rows.Count > 0
            ? rows[0]
            : throw new InvalidOperationException("The upsert returned no row: the store did not hand the written row back.");
    }

    private async Task<long> ExecuteAsync(string sql, List<object?> parameters, CancellationToken cancellationToken)
    {
        await using var command = await _unitOfWork.CommandAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
