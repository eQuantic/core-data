using System.Collections;
using System.Linq.Expressions;
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

            var updateParameters = new List<object?>();
            var updateSet = SetList(updateExpression, updateParameters, existingRow: null);
            var byKeys = KeysAndTheRest(keys, filter, updateParameters);
            await using (var command = await _unitOfWork.CommandAsync(
                             $"UPDATE {Table} SET {updateSet} WHERE {byKeys}", updateParameters, cancellationToken).ConfigureAwait(false))
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var readParameters = new List<object?>();
            var readWhere = KeysWhere(keys, readParameters);
            return await ReadAsync($"SELECT {ColumnList} FROM {Table} WHERE {readWhere}", readParameters, cancellationToken)
                .ConfigureAwait(false);
        }, cancellationToken)).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- upserts (immediate)

    /// <summary>
    ///     Inserts the entity, or updates the row matching it on the target members, atomically, and returns the row as
    ///     stored: one statement where the dialect has one (<c>INSERT … ON CONFLICT</c>, <c>MERGE … WITH (HOLDLOCK)</c>);
    ///     otherwise a read of the target under a lock in a transaction, then the insert or the update — run again when
    ///     a concurrent insert of the same target wins the race (a duplicate key on the target, or a deadlock).
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
        var existingRow = _dialect.UpsertExistingRow(Table);
        var assignments = UpdateAssignments(entity, target, update, parameters, existingRow,
            column => insertColumns.Contains(column) ? insertValues[insertColumns.IndexOf(column)] : Bind(parameters, column.Read(entity)));
        if (assignments.Count == 0)
        {
            // Nothing to write beyond what identifies the row: the update still happens, so the row comes back.
            var first = _dialect.Quote(target[0].Name);
            assignments.Add($"{first} = {existingRow}.{first}");
        }

        var filter = conflictFilter is null
            ? null
            : SqlFilterRenderer.Render(_dialect, _configuration, FilterInterpreter.Interpret(conflictFilter), parameters);

        var sql = _dialect.UpsertSql(Table, insertColumns.Select(column => _dialect.Quote(column.Name)).ToList(), insertValues,
            target.Select(column => _dialect.Quote(column.Name)).ToList(), targetValues, filter, assignments,
            _configuration.Columns.Select(column => _dialect.Quote(column.Name)).ToList());
        return SingleAsync(sql, parameters, cancellationToken);
    }

    private async Task<TEntity> UpsertWithLockingReadAsync(TEntity entity, IReadOnlyList<RelationalColumn> target,
        Expression<Func<TEntity, TEntity>>? update, Expression<Func<TEntity, bool>>? conflictFilter,
        CancellationToken cancellationToken)
    {
        if (_configuration.KeyIsGenerated && target.SequenceEqual(_configuration.Keys))
        {
            throw new NotSupportedException(
                $"{_dialect.System} upserts by reading the target, and a key the store generates has no value to read it by; " +
                "upsert on a unique member instead, or give the entity a client-assigned key.");
        }

        DuplicateKeyException? lostInsert = null;
        for (var attempt = 1; ; attempt++)
        {
            var inserting = false;
            try
            {
                return await _unitOfWork.AtomicallyAsync(async () =>
                {
                    inserting = false;
                    // The target, read under a lock: an existing row stays as it is until this transaction ends, and a
                    // missing one has its place locked where the target is indexed.
                    var readParameters = new List<object?>();
                    var targetWhere = TargetWhere(entity, target, conflictFilter, readParameters);
                    var existing = await ReadAsync(
                        $"SELECT {ColumnList} FROM {Table} WHERE {targetWhere}{_dialect.ForUpdateClause}", readParameters,
                        cancellationToken).ConfigureAwait(false);

                    if (existing.Count == 0)
                    {
                        if (lostInsert is not null)
                        {
                            // The insert lost to a key that is not the target: no retry can take it.
                            throw lostInsert;
                        }

                        inserting = true;
                        var insertParameters = new List<object?>();
                        var columns = InsertColumns();
                        var values = string.Join(", ", columns.Select(column => Bind(insertParameters, column.Read(entity))));
                        var insert = _dialect.InsertSql(Table, string.Join(", ", columns.Select(column => _dialect.Quote(column.Name))),
                            values, (string?)null);
                        await ExecuteAsync(insert, insertParameters, cancellationToken).ConfigureAwait(false);
                        inserting = false;

                        // The row just inserted, by its key — or by the target when the store generated the key.
                        var insertedParameters = new List<object?>();
                        var insertedWhere = _configuration.KeyIsGenerated
                            ? TargetWhere(entity, target, conflictFilter, insertedParameters)
                            : TargetWhere(entity, _configuration.Keys, null, insertedParameters);
                        var inserted = await ReadAsync($"SELECT {ColumnList} FROM {Table} WHERE {insertedWhere}",
                            insertedParameters, cancellationToken).ConfigureAwait(false);
                        return inserted[0];
                    }

                    var row = existing[0];
                    var updateParameters = new List<object?>();
                    var assignments = UpdateAssignments(entity, target, update, updateParameters, existingRow: null,
                        column => Bind(updateParameters, column.Read(entity)));
                    if (assignments.Count > 0)
                    {
                        var byKey = TargetWhere(row, _configuration.Keys, null, updateParameters);
                        await ExecuteAsync($"UPDATE {Table} SET {string.Join(", ", assignments)} WHERE {byKey}", updateParameters,
                            cancellationToken).ConfigureAwait(false);
                    }

                    var storedParameters = new List<object?>();
                    var stored = await ReadAsync(
                        $"SELECT {ColumnList} FROM {Table} WHERE {TargetWhere(row, _configuration.Keys, null, storedParameters)}",
                        storedParameters, cancellationToken).ConfigureAwait(false);
                    return stored[0];
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (inserting && attempt < UpsertAttempts && _dialect.DuplicateKey(exception) is { } duplicate)
            {
                // A concurrent insert of the target won between the read and the insert: the next read finds its row.
                // Inside an explicit transaction only the statement rolled back, so the read runs in it again.
                lostInsert = duplicate;
            }
            catch (Exception exception) when (attempt < UpsertAttempts && _dialect.IsDeadlock(exception)
                                              && !_unitOfWork.InExplicitTransaction)
            {
                // Two upserts locked the target's gap and both tried to insert into it: the store rolled one back.
                await Task.Delay(Random.Shared.Next(5, 20 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (_dialect.DuplicateKey(exception) is { } duplicate)
            {
                throw duplicate;
            }
        }
    }

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
            assignments.Add(type == typeof(int) || type == typeof(long)
                ? $"{quoted} = {current} + 1"
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
            fragments.Add($"{_dialect.Quote(stampColumn.Name)} = {Bind(parameters, stamp.Value)}");
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
            if (keys.Count == 0)
            {
                return 0L;
            }

            var parameters = new List<object?>();
            var sql = statement(parameters);
            return await ExecuteAsync($"{sql} WHERE {KeysAndTheRest(keys, filter, parameters)}", parameters, cancellationToken)
                .ConfigureAwait(false);
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
    ///     The rows by their keys, and the filter's conjuncts that do not read the written table (the ones that do were
    ///     evaluated by the locking read, and the store would refuse them in the write).
    /// </summary>
    private string KeysAndTheRest(IReadOnlyList<object?[]> keys, Expression<Func<TEntity, bool>> filter, List<object?> parameters)
    {
        var byKeys = KeysWhere(keys, parameters);
        var relations = _unitOfWork.Relations;
        return relations.Without(filter, _unitOfWork.GlobalFilter<TEntity>(), typeof(TEntity)) is Expression<Func<TEntity, bool>> rest
            ? $"{byKeys} AND {RelationalSql.Where(_dialect, _configuration, rest, null, parameters, relations)}"
            : byKeys;
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
