using System.Collections;
using System.Linq.Expressions;
using eQuantic.Core.Data.Query;
using eQuantic.Core.Data.Repository;
using eQuantic.Linq.Expressions;
using global::Cassandra;

namespace eQuantic.Core.Data.Cassandra.Repository;

/// <summary>
///     A native Apache Cassandra entity set. Entity inserts are staged on the <see cref="CassandraUnitOfWork" />
///     and applied on commit; set-based writes run immediately as CQL. Cassandra <c>DELETE</c>/<c>UPDATE</c> need
///     the primary key in their <c>WHERE</c>, so those reject non-key filters; enumerating the set is a full-table
///     scan — prefer the repository with a key-scoped <c>QueryOptions</c>.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public sealed class CassandraSet<TEntity> : Data.Repository.ISet<TEntity> where TEntity : class, IEntity
{
    private readonly CassandraUnitOfWork _unitOfWork;
    private readonly ISession _session;
    private readonly CassandraEntityConfiguration _configuration;

    internal CassandraSet(CassandraUnitOfWork unitOfWork, ISession session)
    {
        _unitOfWork = unitOfWork;
        _session = session;
        _configuration = unitOfWork.Configuration<TEntity>();
    }

    // ---------------------------------------------------------------- IQueryable (Cassandra has no arbitrary LINQ)
    public Type ElementType => typeof(TEntity);

    public Expression Expression =>
        throw new NotSupportedException("Cassandra does not support arbitrary LINQ; query through the repository with a QueryOptions.");

    public IQueryProvider Provider =>
        throw new NotSupportedException("Cassandra does not support arbitrary LINQ; query through the repository with a QueryOptions.");

    public IEnumerator<TEntity> GetEnumerator() => Execute().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ---------------------------------------------------------------- reads
    public IEnumerable<TEntity> Execute() => ExecuteAsync().GetAwaiter().GetResult();

    private async Task<List<TEntity>> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var rows = await CassandraStatements.ExecuteAsync(_session, $"SELECT * FROM {_configuration.TableName}", [], null, _unitOfWork.CommandLogger, _unitOfWork.SensitiveLogging).ConfigureAwait(false);
        return rows.Select(row => CassandraMapper.Materialize<TEntity>(_configuration, row)).ToList();
    }

    public TEntity? Find<TKey>(TKey key) => FindAsync(key).GetAwaiter().GetResult();

    public async Task<TEntity?> FindAsync<TKey>(TKey key, CancellationToken cancellationToken = default)
    {
        var rows = await CassandraStatements.ExecuteAsync(_session,
            $"SELECT * FROM {_configuration.TableName} WHERE {_configuration.KeyColumn} = ? LIMIT 1",
            [CassandraValues.ToStored(_configuration.Column(_configuration.KeyColumn), key)], null, _unitOfWork.CommandLogger, _unitOfWork.SensitiveLogging).ConfigureAwait(false);
        var row = rows.FirstOrDefault();
        return row is null ? null : CassandraMapper.Materialize<TEntity>(_configuration, row);
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

    /// <remarks>
    ///     A filter on the primary key alone is one <c>DELETE</c>, counting the rows it found first. A filter on other
    ///     members too becomes a lightweight transaction per row: the rows the key finds are read, and each is deleted
    ///     <c>IF</c> the other members still hold — the count is the deletes Cassandra applied. A soft-delete entity's
    ///     delete stamps <c>DeletedAt</c> instead.
    /// </remarks>
    public async Task<long> DeleteManyAsync(Expression<Func<TEntity, bool>> filter, CancellationToken cancellationToken = default)
    {
        if (EntityLifecycle.IsSoftDelete(typeof(TEntity), _unitOfWork.Conventions))
        {
            return await UpdateManyAsync(filter, EntityLifecycle.SoftDeleteUpdate<TEntity>(_unitOfWork.Conventions, _unitOfWork.Services),
                cancellationToken).ConfigureAwait(false);
        }

        var prepared = await PrepareAsync(filter, "DELETE", cancellationToken).ConfigureAwait(false);
        if (prepared.RowTests.Count > 0)
        {
            return await DeleteRowByRowAsync(prepared, cancellationToken).ConfigureAwait(false);
        }

        filter = prepared.Filter;
        var split = CassandraConditions.Split(_configuration, filter, _unitOfWork.GlobalFilter<TEntity>());
        if (split.Conditions.Count == 0)
        {
            if (split.KeyWhere.Length == 0)
            {
                throw new NotSupportedException("Cassandra DELETE requires the partition key; it cannot filter on non-key columns.");
            }

            var count = await CountAsync(split.KeyWhere, split.KeyValues, cancellationToken).ConfigureAwait(false);
            await CassandraStatements.ExecuteAsync(_session, $"DELETE FROM {_configuration.TableName} WHERE {split.KeyWhere}",
                split.KeyValues, null, _unitOfWork.CommandLogger, _unitOfWork.SensitiveLogging).ConfigureAwait(false);
            return count;
        }

        RequireConditionalWrite(split, "DELETE");
        var rows = await KeyedRowsAsync(split, cancellationToken).ConfigureAwait(false);
        var keys = CassandraMapper.PrimaryKey(_configuration);
        var condition = string.Join(" AND ", split.Conditions.Select(clause => clause.Cql));
        var global = _unitOfWork.GlobalFilter<TEntity>();
        var matches = (global is null ? filter : filter.AndAlso(global)).Compile();
        var applied = await Task.WhenAll(rows.Select(async row =>
        {
            object?[] values = [.. KeyValues(row, keys), .. split.Conditions.SelectMany(clause => clause.Values)];
            var cql = $"DELETE FROM {_configuration.TableName} WHERE {string.Join(" AND ", keys.Select(key => $"{key} = ?"))} IF {condition}";
            for (var attempt = 1; attempt <= ConditionalAttempts; attempt++)
            {
                switch ((await TryConditionalAsync(cql, values).ConfigureAwait(false)).Applied)
                {
                    case true:
                        return 1L;
                    case false:
                        return 0L;
                }

                // The transaction timed out under contention, its outcome unknown: a serial read settles it. A row that is
                // gone was deleted (by this delete, or by one with the same effect); one that still matches is deleted again.
                var settled = await ReadRowAsync(row, keys).ConfigureAwait(false);
                if (settled is null)
                {
                    return 1L;
                }

                if (!matches(CassandraMapper.Materialize<TEntity>(_configuration, settled)))
                {
                    return 0L;
                }

                await BackoffAsync(attempt).ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                $"A row of '{_configuration.TableName}' kept contending while it was being deleted; {ConditionalAttempts} attempts did not settle.");
        })).ConfigureAwait(false);
        return applied.Sum();
    }

    public long UpdateMany(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateExpression) =>
        UpdateManyAsync(filter, updateExpression).GetAwaiter().GetResult();

    /// <remarks>
    ///     A filter on the primary key alone, with an update CQL applies in place, is one <c>UPDATE</c>, counting the rows
    ///     it found first (Cassandra's <c>UPDATE</c> is an upsert: a row deleted in between is written again). A filter on
    ///     other members too — or an update CQL cannot apply in place, such as <c>x.N + 1</c> on a column that is not a
    ///     counter — becomes a lightweight transaction per row: the rows the key finds are read, and each is updated
    ///     <c>IF</c> the other members still hold (and, for a value computed from the row, <c>IF</c> it still holds what
    ///     was read). The count is the updates Cassandra applied.
    /// </remarks>
    public async Task<long> UpdateManyAsync(Expression<Func<TEntity, bool>> filter,
        Expression<Func<TEntity, TEntity>> updateExpression, CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareAsync(filter, "UPDATE", cancellationToken).ConfigureAwait(false);
        if (prepared.RowTests.Count > 0)
        {
            return (await UpdateConditionallyAsync(prepared.Split!, prepared.Filter, Assignments(updateExpression), cancellationToken,
                prepared).ConfigureAwait(false)).Count;
        }

        filter = prepared.Filter;
        var split = CassandraConditions.Split(_configuration, filter, _unitOfWork.GlobalFilter<TEntity>());
        var assignments = Assignments(updateExpression);
        if (split.Conditions.Count == 0 && !NeedsReadOfRow(assignments))
        {
            if (split.KeyWhere.Length == 0)
            {
                throw new NotSupportedException("Cassandra UPDATE requires the primary key; it cannot filter on non-key columns.");
            }

            var (set, setValues) = CassandraUpdate.Build(_configuration, assignments);
            if (!split.NamesEveryKey)
            {
                // An UPDATE names every primary-key column: the rows the partition restriction finds are read for their
                // keys, and each is updated by its own (Cassandra's UPDATE is an upsert, as the single UPDATE is).
                var keyed = await KeyedRowsAsync(split, cancellationToken).ConfigureAwait(false);
                var primary = CassandraMapper.PrimaryKey(_configuration);
                var byKey = string.Join(" AND ", primary.Select(key => $"{key} = ?"));
                await Task.WhenAll(keyed.Select(row => CassandraStatements.ExecuteAsync(_session,
                    $"UPDATE {_configuration.TableName} SET {set} WHERE {byKey}", [.. setValues, .. KeyValues(row, primary)], null,
                    _unitOfWork.CommandLogger, _unitOfWork.SensitiveLogging))).ConfigureAwait(false);
                return keyed.Count;
            }

            var count = await CountAsync(split.KeyWhere, split.KeyValues, cancellationToken).ConfigureAwait(false);
            await CassandraStatements.ExecuteAsync(_session,
                $"UPDATE {_configuration.TableName} SET {set} WHERE {split.KeyWhere}", setValues.Concat(split.KeyValues).ToArray(), null,
                _unitOfWork.CommandLogger, _unitOfWork.SensitiveLogging).ConfigureAwait(false);
            return count;
        }

        return (await UpdateConditionallyAsync(split, filter, assignments, cancellationToken).ConfigureAwait(false)).Count;
    }

    // ---------------------------------------------------------------- returning updates (immediate)

    /// <summary>
    ///     Applies the update as a lightweight transaction per row — <c>IF EXISTS</c>, or <c>IF</c> the filter's other
    ///     members still hold, and <c>IF</c> every value computed from the row still holds what was read — and returns
    ///     each row it applied to as it wrote it: the row read, with the update's values.
    /// </summary>
    internal async Task<IReadOnlyList<TEntity>> UpdateAndGetAsync(Expression<Func<TEntity, bool>> filter,
        Expression<Func<TEntity, TEntity>> updateExpression, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(filter, "UPDATE", cancellationToken).ConfigureAwait(false);
        var split = prepared.Split ?? CassandraConditions.Split(_configuration, prepared.Filter, _unitOfWork.GlobalFilter<TEntity>());
        return await UpdateConditionallyAsync(split, prepared.Filter, Assignments(updateExpression), cancellationToken, prepared)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     A set-based write's filter prepared: normalized, and each test across a relation resolved into the related keys
    ///     (read first; a write carries no opt-ins, so the related read has to be one the table serves natively). A resolved
    ///     test the key restriction or a lightweight transaction's <c>IF</c> can hold (an <c>IN</c> on the key) stays in the
    ///     filter, and the write finds its rows by it as by any other. One they cannot hold — a negated relation, a
    ///     <c>NOT IN</c> — is a <b>row test</b>: the rows the rest of the filter finds (under the write's partition) are read, a
    ///     row whose key the related read returned is left out, and each other row is written by its full key in a lightweight
    ///     transaction that also holds every column the test read at the value read.
    /// </summary>
    private async Task<PreparedWrite> PrepareAsync(Expression<Func<TEntity, bool>> filter, string statement, CancellationToken cancellationToken)
    {
        var normalized = PredicateNormalizer.Normalize(filter);
        var parameter = normalized.Parameters[0];
        var navigations = CassandraPredicates.Navigations(_unitOfWork);
        var pushed = new List<Expression>();
        var rowTests = new List<Expression>();
        foreach (var conjunct in Conjuncts(normalized.Body))
        {
            var lambda = Expression.Lambda<Func<TEntity, bool>>(conjunct, parameter);
            if (RelationPredicates.Find(lambda, navigations).Count == 0)
            {
                pushed.Add(conjunct);
                continue;
            }

            var resolved = await CassandraPredicates.ResolveAsync(lambda, _unitOfWork, null, cancellationToken).ConfigureAwait(false);
            (Splits(resolved) ? pushed : rowTests).Add(resolved.Body);
        }

        var whole = Expression.Lambda<Func<TEntity, bool>>(pushed.Concat(rowTests).Aggregate(Expression.AndAlso), parameter);
        if (rowTests.Count == 0)
        {
            return new PreparedWrite(whole, null, [], []);
        }

        if (pushed.Count == 0)
        {
            throw new NotSupportedException(
                $"A conditional {statement} reads the rows by their key first, and the filter names none of it: a negated relation " +
                "leaves rows out of the ones the rest of the filter finds, so the filter has to name the partition key too.");
        }

        var split = CassandraConditions.Split(_configuration,
            Expression.Lambda<Func<TEntity, bool>>(pushed.Aggregate(Expression.AndAlso), parameter), _unitOfWork.GlobalFilter<TEntity>());
        RequireConditionalWrite(split, statement);
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        new ColumnReader(parameter, member =>
        {
            var column = _configuration.ColumnFor(member);
            if (!_configuration.IsKey(column) && _configuration.Column(column) is not null)
            {
                held.Add(column);
            }
        }).Visit(Expression.Lambda(rowTests.Aggregate(Expression.AndAlso), parameter));
        return new PreparedWrite(whole, split,
            rowTests.Select(test => Expression.Lambda<Func<TEntity, bool>>(test, parameter).Compile()).ToList(), held.ToList());
    }

    private bool Splits(Expression<Func<TEntity, bool>> filter)
    {
        try
        {
            CassandraConditions.Split(_configuration, filter, null);
            return true;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static IEnumerable<Expression> Conjuncts(Expression body) =>
        body is BinaryExpression { NodeType: ExpressionType.AndAlso } and ? Conjuncts(and.Left).Concat(Conjuncts(and.Right)) : [body];

    /// <summary>
    ///     The <c>IF</c> clauses holding the columns a row test read at the values the row had (<c>col = null</c> for an unset
    ///     one), so the row is written only while the test still holds on it.
    /// </summary>
    private (List<string> Clauses, List<object?> Values) Held(PreparedWrite? prepared, TEntity current)
    {
        var clauses = new List<string>();
        var values = new List<object?>();
        foreach (var column in prepared?.Held ?? [])
        {
            var stored = CassandraValues.ToStored(_configuration.Column(column), Member(current, _configuration.MemberFor(column)));
            clauses.Add(stored is null ? $"{column} = null" : $"{column} = ?");
            if (stored is not null)
            {
                values.Add(stored);
            }
        }

        return (clauses, values);
    }

    /// <summary>
    ///     A delete with row tests: the rows the key restriction finds, the ones a row test leaves out skipped, and each other
    ///     deleted by its full key <c>IF</c> the filter's conditions and the columns the tests read still hold (<c>IF EXISTS</c>
    ///     when nothing else is held). A transaction that does not apply reads the row again and decides again.
    /// </summary>
    private async Task<long> DeleteRowByRowAsync(PreparedWrite prepared, CancellationToken cancellationToken)
    {
        var split = prepared.Split!;
        var keys = CassandraMapper.PrimaryKey(_configuration);
        var global = _unitOfWork.GlobalFilter<TEntity>();
        var matches = (global is null ? prepared.Filter : prepared.Filter.AndAlso(global)).Compile();
        var rows = await KeyedRowsAsync(split, cancellationToken).ConfigureAwait(false);
        var deleted = await Task.WhenAll(rows.Select(async row =>
        {
            for (var attempt = 1; attempt <= ConditionalAttempts; attempt++)
            {
                var current = CassandraMapper.Materialize<TEntity>(_configuration, row);
                if (!prepared.RowTests.All(test => test(current)))
                {
                    return 0L;
                }

                var (heldClauses, heldValues) = Held(prepared, current);
                var conditions = split.Conditions.Select(clause => clause.Cql).Concat(heldClauses).ToList();
                var cql = $"DELETE FROM {_configuration.TableName} WHERE {string.Join(" AND ", keys.Select(key => $"{key} = ?"))} " +
                          (conditions.Count == 0 ? "IF EXISTS" : $"IF {string.Join(" AND ", conditions)}");
                var (applied, timeout) = await TryConditionalAsync(cql,
                    [.. KeyValues(row, keys), .. split.Conditions.SelectMany(clause => clause.Values), .. heldValues]).ConfigureAwait(false);
                if (applied == true)
                {
                    return 1L;
                }

                // Not applied, or its outcome unknown: a serial read decides. A row that is gone was deleted when the
                // transaction timed out (by this delete, or one with the same effect), and was not this delete's otherwise.
                var fresh = await ReadRowAsync(row, keys).ConfigureAwait(false);
                if (fresh is null)
                {
                    return timeout is null ? 0L : 1L;
                }

                if (!matches(CassandraMapper.Materialize<TEntity>(_configuration, fresh)))
                {
                    return 0L;
                }

                row = fresh;
                await BackoffAsync(attempt).ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                $"A row of '{_configuration.TableName}' kept changing while it was being deleted; {ConditionalAttempts} attempts did not settle.");
        })).ConfigureAwait(false);
        return deleted.Sum();
    }

    /// <summary>The filter a write runs: the whole (resolved) filter, its key restriction when row tests apply, the tests and the columns they read.</summary>
    private sealed record PreparedWrite(Expression<Func<TEntity, bool>> Filter, CassandraConditions? Split,
        IReadOnlyList<Func<TEntity, bool>> RowTests, IReadOnlyList<string> Held);

    /// <summary>Hands each member of the parameter an expression reads to a callback.</summary>
    private sealed class ColumnReader(ParameterExpression parameter, Action<string> read) : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression == parameter)
            {
                read(node.Member.Name);
                return node;
            }

            return base.VisitMember(node);
        }
    }

    /// <summary>The update's assignments, with the update's lifecycle stamps unless it assigns them.</summary>
    private List<UpdateAssignment> Assignments(Expression<Func<TEntity, TEntity>> updateExpression)
    {
        var assignments = UpdateInterpreter.Interpret(updateExpression).ToList();
        foreach (var stamp in EntityLifecycle.UpdateStamps(typeof(TEntity), _unitOfWork.Conventions, _unitOfWork.Services))
        {
            if (!assignments.Any(assignment => assignment.Name == stamp.Name)
                && _configuration.Columns.Any(column => CassandraEntityConfiguration.Same(column.Member, stamp.Name)))
            {
                assignments.Add(stamp);
            }
        }

        return assignments;
    }

    /// <summary>Whether an assignment computes its value from the row in a way CQL cannot apply in place.</summary>
    private bool NeedsReadOfRow(IEnumerable<UpdateAssignment> assignments) => assignments.Any(assignment =>
        assignment is IncrementAssignment or MultiplyAssignment && !_configuration.IsCounter(_configuration.ColumnFor(assignment.Name)));

    private async Task<IReadOnlyList<TEntity>> UpdateConditionallyAsync(CassandraConditions split,
        Expression<Func<TEntity, bool>> filter, IReadOnlyList<UpdateAssignment> assignments, CancellationToken cancellationToken,
        PreparedWrite? prepared = null)
    {
        RequireConditionalWrite(split, "UPDATE");
        foreach (var assignment in assignments)
        {
            var column = _configuration.ColumnFor(assignment.Name);
            if (_configuration.IsKey(column) || _configuration.IsStoreGenerated(column))
            {
                throw new NotSupportedException(
                    $"An update cannot assign '{assignment.Name}': the primary key identifies the row, and the store assigns a store-generated column.");
            }
        }

        var global = _unitOfWork.GlobalFilter<TEntity>();
        var matches = (global is null ? filter : filter.AndAlso(global)).Compile();
        var keys = CassandraMapper.PrimaryKey(_configuration);
        var rows = await KeyedRowsAsync(split, cancellationToken).ConfigureAwait(false);

        var updated = await Task.WhenAll(rows.Select(async row =>
        {
            for (var attempt = 1; attempt <= ConditionalAttempts; attempt++)
            {
                var current = CassandraMapper.Materialize<TEntity>(_configuration, row);
                if (prepared is not null && !prepared.RowTests.All(test => test(current)))
                {
                    // A row a negated relation leaves out (its key among the ones the related read returned).
                    return null;
                }

                var next = CassandraMapper.Materialize<TEntity>(_configuration, row);
                foreach (var assignment in assignments)
                {
                    UpdateApplication.Apply(next, assignment);
                }

                // Each assigned column takes the value computed here; one computed from the row is written only while the
                // row still holds what it was computed from.
                var sets = new List<string>();
                var values = new List<object?>();
                var conditions = split.Conditions.Select(clause => clause.Cql).ToList();
                var conditionValues = split.Conditions.SelectMany(clause => clause.Values).ToList();
                var (heldClauses, heldValues) = Held(prepared, current);
                conditions.AddRange(heldClauses);
                conditionValues.AddRange(heldValues);
                foreach (var assignment in assignments)
                {
                    var column = _configuration.Column(_configuration.ColumnFor(assignment.Name))!;
                    sets.Add($"{column.Name} = ?");
                    values.Add(CassandraValues.ToStored(column, Member(next, assignment.Name)));
                    if (assignment is not SetAssignment)
                    {
                        var read = CassandraValues.ToStored(column, Member(current, assignment.Name));
                        conditions.Add(read is null ? $"{column.Name} = null" : $"{column.Name} = ?");
                        if (read is not null)
                        {
                            conditionValues.Add(read);
                        }
                    }
                }

                var cql = $"UPDATE {_configuration.TableName} SET {string.Join(", ", sets)} " +
                          $"WHERE {string.Join(" AND ", keys.Select(key => $"{key} = ?"))} " +
                          (conditions.Count == 0 ? "IF EXISTS" : $"IF {string.Join(" AND ", conditions)}");
                var (applied, timeout) = await TryConditionalAsync(cql, [.. values, .. KeyValues(row, keys), .. conditionValues]).ConfigureAwait(false);
                if (applied == true)
                {
                    return next;
                }

                // Not applied: the row stopped matching, went away, or changed what a value was computed from. Read it
                // again: when it still matches, compute again; otherwise it is not this update's.
                var fresh = await ReadRowAsync(row, keys).ConfigureAwait(false);
                if (fresh is null)
                {
                    return null;
                }

                var freshEntity = CassandraMapper.Materialize<TEntity>(_configuration, fresh);
                if (timeout is not null && Settled(freshEntity, next, assignments, timeout))
                {
                    // A timed-out transaction (its outcome unknown) that the serial read finds applied: the row holds what
                    // this update writes. A value computed from the row is never taken for applied this way — see Settled.
                    return next;
                }

                if (!matches(freshEntity))
                {
                    return null;
                }

                row = fresh;
                await BackoffAsync(attempt).ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                $"A row of '{_configuration.TableName}' kept changing while it was being updated; {ConditionalAttempts} attempts did not settle.");
        })).ConfigureAwait(false);

        return updated.Where(entity => entity is not null).Select(entity => entity!).ToList();
    }

    private const int ConditionalAttempts = 50;

    /// <summary>Refuses a conditional write Cassandra cannot run: on a counter table, or without the partition in the filter.</summary>
    private void RequireConditionalWrite(CassandraConditions split, string statement)
    {
        if (_configuration.CounterColumns.Count > 0)
        {
            throw new NotSupportedException(
                $"'{typeof(TEntity).Name}' is a counter table, and Cassandra takes no conditions on one: a counter {statement} " +
                "filters on the primary key alone, and its counters only move by increments.");
        }

        if (!split.PinsPartition)
        {
            throw new NotSupportedException(
                $"A conditional {statement} reads the rows by their key first, and Cassandra reads a table by its partition: " +
                "name the partition key in the filter (the other members become the lightweight transaction's condition).");
        }
    }

    /// <summary>
    ///     The rows the key restriction finds, read at the serial consistency a lightweight transaction writes at — or, when
    ///     the restriction names several partitions (Cassandra takes a serial read of one partition at a time), at the
    ///     session's consistency: each row's own transaction still checks its condition before it writes.
    /// </summary>
    private async Task<List<global::Cassandra.Row>> KeyedRowsAsync(CassandraConditions split, CancellationToken cancellationToken)
    {
        ConsistencyLevel? consistency = ConsistencyLevel.Serial;
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var rows = await CassandraStatements.ExecuteAsync(_session,
                    $"SELECT * FROM {_configuration.TableName} WHERE {split.KeyWhere}", split.KeyValues, consistency,
                    _unitOfWork.CommandLogger, _unitOfWork.SensitiveLogging).ConfigureAwait(false);
                return rows.ToList();
            }
            catch (InvalidQueryException exception) when (consistency is not null
                                                          && exception.Message.Contains("one partition", StringComparison.OrdinalIgnoreCase))
            {
                consistency = null;
            }
            catch (Exception exception) when (exception is ReadTimeoutException or WriteTimeoutException && attempt < ConditionalAttempts)
            {
                // A serial read takes part in Paxos, and times out under the same contention: read again.
                await BackoffAsync(attempt).ConfigureAwait(false);
            }
        }
    }

    private Task<global::Cassandra.Row?> ReadRowAsync(global::Cassandra.Row row, IReadOnlyList<string> keys) =>
        ReadRowAsync(KeyValues(row, keys), keys);

    private Task<global::Cassandra.Row?> ReadRowAsync(TEntity entity, IReadOnlyList<string> keys) =>
        ReadRowAsync(keys.Select(key => CassandraValues.ToStored(_configuration.Column(key), Member(entity, _configuration.MemberFor(key))))
            .ToArray(), keys);

    /// <summary>
    ///     A row by its primary key, at the serial consistency: the read takes part in Paxos, so a transaction in flight on
    ///     the row is completed (or abandoned) first, and the row read is settled. A read that times out under the same
    ///     contention is read again.
    /// </summary>
    private async Task<global::Cassandra.Row?> ReadRowAsync(object?[] keyValues, IReadOnlyList<string> keys)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var rows = await CassandraStatements.ExecuteAsync(_session,
                    $"SELECT * FROM {_configuration.TableName} WHERE {string.Join(" AND ", keys.Select(key => $"{key} = ?"))}",
                    keyValues, ConsistencyLevel.Serial, _unitOfWork.CommandLogger, _unitOfWork.SensitiveLogging).ConfigureAwait(false);
                return rows.FirstOrDefault();
            }
            catch (Exception exception) when (exception is ReadTimeoutException or WriteTimeoutException && attempt < ConditionalAttempts)
            {
                await BackoffAsync(attempt).ConfigureAwait(false);
            }
        }
    }

    /// <summary>A row's primary key values, as stored.</summary>
    private static object?[] KeyValues(global::Cassandra.Row row, IReadOnlyList<string> keys) =>
        keys.Select(key => row.GetValue<object>(key.ToLowerInvariant())).ToArray();

    private static object? Member(TEntity entity, string member) =>
        typeof(TEntity).GetProperty(member, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                                            | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(entity);

    // ---------------------------------------------------------------- upserts (immediate)

    /// <summary>
    ///     Inserts the entity (<c>INSERT … IF NOT EXISTS</c>) or, when its primary key is taken, updates that row with a
    ///     lightweight transaction (<c>UPDATE … IF EXISTS</c>, or <c>IF</c> each value computed from the row still holds
    ///     what was read), and returns the row as written. Cassandra holds nothing unique but the primary key, so that is
    ///     the only target an upsert takes.
    /// </summary>
    internal async Task<TEntity> UpsertAsync(TEntity entity, IReadOnlyList<string>? on,
        Expression<Func<TEntity, TEntity>>? updateExpression, Expression<Func<TEntity, bool>>? conflictFilter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var keys = CassandraMapper.PrimaryKey(_configuration);
        if (on is not null)
        {
            var target = on.Select(_configuration.ColumnFor).ToList();
            if (target.Count != keys.Count || !keys.All(key => target.Any(column => CassandraEntityConfiguration.Same(column, key))))
            {
                throw new NotSupportedException(
                    $"Cassandra holds no key unique but a table's primary key ({string.Join(", ", keys)}), so it cannot upsert on " +
                    $"{string.Join(", ", on)}: the members that identify a row belong in the primary key of a table of their own.");
            }
        }

        if (_configuration.CounterColumns.Count > 0)
        {
            throw new NotSupportedException(
                $"'{typeof(TEntity).Name}' maps counter columns; a counter table has no inserts — mutate it through UpdateMany increments.");
        }

        EntityLifecycle.StampForInsert(entity, _unitOfWork.Conventions, _unitOfWork.Services);
        if (_configuration.ConcurrencyColumn is { } token && Convert.ToInt64(Member(entity, _configuration.MemberFor(token)) ?? 0L) == 0)
        {
            var property = typeof(TEntity).GetProperty(_configuration.MemberFor(token),
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase)!;
            property.SetValue(entity, Convert.ChangeType(1L, Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType));
        }

        var filter = conflictFilter?.Compile();
        var sequencesAssigned = false;
        for (var attempt = 1; attempt <= ConditionalAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sequencesAssigned)
            {
                await _unitOfWork.AssignSequencesAsync(_configuration, [entity]).ConfigureAwait(false);
                sequencesAssigned = true;
            }

            var (insert, insertValues) = CassandraMapper.BuildInsert(_configuration, entity);
            global::Cassandra.Row? inserted;
            try
            {
                inserted = (await CassandraStatements.ExecuteAsync(_session, insert + " IF NOT EXISTS", insertValues, null,
                    _unitOfWork.CommandLogger, _unitOfWork.SensitiveLogging).ConfigureAwait(false)).First();
            }
            catch (WriteTimeoutException timeout) when (timeout.WriteType == "CAS")
            {
                // Timed out under contention, its outcome unknown: a serial read settles it. The row this insert wrote is
                // the entity's (its own values), and then the insert applied; any other row makes this the update.
                inserted = await ReadRowAsync(entity, keys).ConfigureAwait(false);
                if (inserted is null)
                {
                    await BackoffAsync(attempt).ConfigureAwait(false);
                    continue;
                }

                if (Inserted(CassandraMapper.Materialize<TEntity>(_configuration, inserted), entity))
                {
                    CassandraMapper.AssignReadBack(_configuration, entity, inserted);
                    return entity;
                }
            }

            if (inserted.Length > 0 && inserted.GetColumn("[applied]") is not null && inserted.GetValue<bool>("[applied]"))
            {
                if (_configuration.StoreGenerated.Count > 0)
                {
                    var (readBack, readBackValues) = CassandraMapper.BuildReadBack(_configuration, entity);
                    var read = await CassandraStatements.ExecuteAsync(_session, readBack, readBackValues, ConsistencyLevel.Serial,
                        _unitOfWork.CommandLogger, _unitOfWork.SensitiveLogging).ConfigureAwait(false);
                    CassandraMapper.AssignReadBack(_configuration, entity, read.FirstOrDefault());
                }

                return entity;
            }

            // The key is taken: the transaction answered with the row that holds it.
            var existing = CassandraMapper.Materialize<TEntity>(_configuration, inserted);
            if (filter is not null && !filter(existing))
            {
                throw new DuplicateKeyException(
                    $"The store refused the write: the primary key ({string.Join(", ", keys)}) is taken by a row the conflict " +
                    "filter does not match, and Cassandra holds no other key the insert could take.", null, null);
            }

            if (await UpdateExistingAsync(entity, existing, inserted, keys, updateExpression).ConfigureAwait(false) is { } written)
            {
                return written;
            }

            await BackoffAsync(attempt).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"The row of '{_configuration.TableName}' kept changing while it was being upserted; {ConditionalAttempts} attempts did not settle.");
    }

    /// <summary>The update of an upsert, as a lightweight transaction on the row read; <c>null</c> when it did not apply.</summary>
    private async Task<TEntity?> UpdateExistingAsync(TEntity entity, TEntity existing, global::Cassandra.Row row,
        IReadOnlyList<string> keys, Expression<Func<TEntity, TEntity>>? updateExpression)
    {
        var next = CassandraMapper.Materialize<TEntity>(_configuration, row);
        var assignments = new List<UpdateAssignment>();
        if (updateExpression is not null)
        {
            assignments.AddRange(Assignments(updateExpression));
        }
        else
        {
            var creation = EntityLifecycle.CreationMembers(typeof(TEntity), _unitOfWork.Conventions);
            foreach (var column in _configuration.Columns)
            {
                if (_configuration.IsKey(column.Name) || _configuration.IsStoreGenerated(column.Name)
                    || CassandraEntityConfiguration.Same(column.Name, _configuration.ConcurrencyColumn ?? string.Empty)
                    || creation.Any(member => CassandraEntityConfiguration.Same(member, column.Member)))
                {
                    continue;
                }

                if (typeof(TEntity).GetProperty(column.Member, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                                                                | System.Reflection.BindingFlags.IgnoreCase) is { } property)
                {
                    assignments.Add(new SetAssignment(property, property.GetValue(entity)));
                }
            }

            foreach (var stamp in EntityLifecycle.UpdateStamps(typeof(TEntity), _unitOfWork.Conventions, _unitOfWork.Services))
            {
                assignments.RemoveAll(assignment => assignment.Name == stamp.Name);
                assignments.Add(stamp);
            }
        }

        foreach (var assignment in assignments)
        {
            var column = _configuration.ColumnFor(assignment.Name);
            if (_configuration.IsKey(column) || _configuration.IsStoreGenerated(column))
            {
                throw new NotSupportedException(
                    $"An upsert's update cannot assign '{assignment.Name}': the primary key identifies the row, and the store assigns a store-generated column.");
            }

            UpdateApplication.Apply(next, assignment);
        }

        var sets = new List<string>();
        var values = new List<object?>();
        var conditions = new List<string>();
        var conditionValues = new List<object?>();
        foreach (var assignment in assignments)
        {
            var column = _configuration.Column(_configuration.ColumnFor(assignment.Name))!;
            sets.Add($"{column.Name} = ?");
            values.Add(CassandraValues.ToStored(column, Member(next, assignment.Name)));
            if (assignment is not SetAssignment)
            {
                var read = CassandraValues.ToStored(column, Member(existing, assignment.Name));
                conditions.Add(read is null ? $"{column.Name} = null" : $"{column.Name} = ?");
                if (read is not null)
                {
                    conditionValues.Add(read);
                }
            }
        }

        if (_configuration.ConcurrencyColumn is { } token)
        {
            var version = Convert.ToInt64(Member(existing, _configuration.MemberFor(token)) ?? 0L);
            sets.Add($"{token} = ?");
            values.Add(version + 1);
            conditions.Add($"{token} = ?");
            conditionValues.Add(version);
            var property = typeof(TEntity).GetProperty(_configuration.MemberFor(token),
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase)!;
            property.SetValue(next, Convert.ChangeType(version + 1, Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType));
        }

        if (sets.Count == 0)
        {
            // Nothing to write beyond the key: the row is the row read.
            return existing;
        }

        var cql = $"UPDATE {_configuration.TableName} SET {string.Join(", ", sets)} " +
                  $"WHERE {string.Join(" AND ", keys.Select(key => $"{key} = ?"))} " +
                  (conditions.Count == 0 ? "IF EXISTS" : $"IF {string.Join(" AND ", conditions)}");
        var (applied, timeout) = await TryConditionalAsync(cql, [.. values, .. KeyValues(row, keys), .. conditionValues]).ConfigureAwait(false);
        if (timeout is not null && await ReadRowAsync(row, keys).ConfigureAwait(false) is { } settled
                                && Settled(CassandraMapper.Materialize<TEntity>(_configuration, settled), next, assignments, timeout))
        {
            // Timed out, and the serial read finds the row holding what this update writes.
            return next;
        }

        return applied == true ? next : null;
    }

    // ---------------------------------------------------------------- lightweight transactions under contention

    /// <summary>
    ///     Runs a lightweight transaction: <c>true</c> when Cassandra applied it, <c>false</c> when its condition did not
    ///     hold, and <c>null</c> when it timed out under contention (<c>WriteTimeoutException</c>, write type <c>CAS</c>):
    ///     then its outcome is unknown until a serial read settles it.
    /// </summary>
    private async Task<(bool? Applied, WriteTimeoutException? Timeout)> TryConditionalAsync(string cql, object?[] values)
    {
        try
        {
            var result = await CassandraStatements.ExecuteAsync(_session, cql, values, null, _unitOfWork.CommandLogger,
                _unitOfWork.SensitiveLogging).ConfigureAwait(false);
            return (result.First().GetValue<bool>("[applied]"), null);
        }
        catch (WriteTimeoutException timeout) when (timeout.WriteType == "CAS")
        {
            return (null, timeout);
        }
    }

    /// <summary>
    ///     Whether a row read after a timed-out update holds what the update writes — and so the update applied, or one
    ///     with the same effect did. Only an update of values set outright is settled this way, and written again when it
    ///     did not apply: a value computed from the row (<c>x.N + 1</c>, the bumped version) cannot tell this update's write
    ///     from another's of the same sum, nor a later write over it from no write at all, so its timeout reaches the
    ///     caller as a <see cref="TimeoutException" /> with the driver's inside.
    /// </summary>
    private bool Settled(TEntity stored, TEntity next, IReadOnlyList<UpdateAssignment> assignments, WriteTimeoutException timeout)
    {
        if (assignments.Any(assignment => assignment is not SetAssignment) || _configuration.ConcurrencyColumn is not null)
        {
            throw new TimeoutException(
                $"A lightweight transaction on '{_configuration.TableName}' timed out under contention, and Cassandra cannot say " +
                "whether it applied: a value it computed from the row (an increment, the version) is not written a second time. " +
                "Read the row and decide.", timeout);
        }

        return assignments.All(assignment => Same(Member(stored, assignment.Name), Member(next, assignment.Name)));
    }

    /// <summary>Whether a row read after a timed-out insert is the entity's: every column it wrote holds its value.</summary>
    private bool Inserted(TEntity stored, TEntity entity) =>
        _configuration.Columns
            .Where(column => !_configuration.IsStoreGenerated(column.Name))
            .All(column => Same(Member(stored, column.Member), Member(entity, column.Member)));

    /// <summary>Equality of two values as Cassandra stores them: timestamps to the millisecond, collections by their items.</summary>
    private static bool Same(object? left, object? right) => (left, right) switch
    {
        (null, null) => true,
        (DateTime a, DateTime b) => Math.Abs((a.ToUniversalTime() - b.ToUniversalTime()).TotalMilliseconds) < 1,
        (DateTimeOffset a, DateTimeOffset b) => Math.Abs((a - b).TotalMilliseconds) < 1,
        (System.Collections.IEnumerable a, System.Collections.IEnumerable b) when left is not string =>
            a.Cast<object?>().SequenceEqual(b.Cast<object?>()),
        _ => Equals(left, right),
    };

    /// <summary>A jittered pause after a lost race, so contending transactions stop proposing in lockstep.</summary>
    private static Task BackoffAsync(int attempt) => Task.Delay(Random.Shared.Next(1, 10 * Math.Min(attempt, 10) + 2));

    // ---------------------------------------------------------------- inserts skipping duplicates (immediate)

    /// <summary>
    ///     Inserts every entity with a lightweight transaction (<c>INSERT … IF NOT EXISTS</c>), concurrently, and counts
    ///     the inserts Cassandra applied. A primary key already stored is not applied: with
    ///     <paramref name="skipDuplicates" /> the entity is left out; without it the call fails with
    ///     <see cref="DuplicateKeyException" /> once every insert has answered — each is its own write, so the others
    ///     stay written.
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
            // A Cassandra insert stamps both, as the staged one does.
            EntityLifecycle.StampForInsert(entity, _unitOfWork.Conventions, _unitOfWork.Services);
            EntityLifecycle.StampForUpdate(entity, _unitOfWork.Conventions, _unitOfWork.Services);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var applied = await Task.WhenAll(entities.Select(entity => _unitOfWork.InsertIfNotExistsAsync(entity))).ConfigureAwait(false);
        var inserted = applied.LongCount(insert => insert);
        if (!skipDuplicates && inserted < entities.Count)
        {
            throw new DuplicateKeyException(
                $"The store refused the write: {entities.Count - inserted} of the {entities.Count} rows have a primary key " +
                $"({string.Join(", ", CassandraMapper.PrimaryKey(_configuration))}) already stored, and their INSERT … IF NOT EXISTS " +
                "did not apply; the others were written.", null, null);
        }

        return inserted;
    }

    private async Task<long> CountAsync(string where, object?[] values, CancellationToken cancellationToken)
    {
        var cql = $"SELECT COUNT(*) FROM {_configuration.TableName}" + (where.Length > 0 ? $" WHERE {where}" : string.Empty);
        var rows = await CassandraStatements.ExecuteAsync(_session, cql, values, null, _unitOfWork.CommandLogger, _unitOfWork.SensitiveLogging).ConfigureAwait(false);
        return rows.First().GetValue<long>(0);
    }
}
