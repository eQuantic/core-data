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
    ///     A filter on the primary key alone is one <c>DELETE</c> (one per chunk of a long <c>IN</c>), counting the rows it
    ///     found first. A filter on other members too becomes a lightweight transaction per row: the rows the key finds are
    ///     read, and each is deleted <c>IF</c> the other members still hold — one partition's transactions one at a time — and
    ///     the count is the deletes Cassandra applied. A soft-delete entity's delete stamps <c>DeletedAt</c> instead.
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

            var count = await CountAsync(split, cancellationToken).ConfigureAwait(false);
            foreach (var (where, values) in split.KeyChunks)
            {
                await CassandraStatements.ExecuteAsync(_session, $"DELETE FROM {_configuration.TableName} WHERE {where}",
                    values, null, _unitOfWork.CommandLogger, _unitOfWork.SensitiveLogging).ConfigureAwait(false);
            }

            return count;
        }

        RequireConditionalWrite(split, "DELETE");
        var rows = await KeyedRowsAsync(split, cancellationToken).ConfigureAwait(false);
        var keys = CassandraMapper.PrimaryKey(_configuration);
        var condition = string.Join(" AND ", split.Conditions.Select(clause => clause.Cql));
        var global = _unitOfWork.GlobalFilter<TEntity>();
        var matches = (global is null ? filter : filter.AndAlso(global)).Compile();
        var applied = await PerPartitionAsync(rows, async row =>
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

                await CassandraLightweightTransactions.BackoffAsync(attempt).ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                $"A row of '{_configuration.TableName}' kept contending while it was being deleted; {ConditionalAttempts} attempts did not settle.");
        }).ConfigureAwait(false);
        return applied.Sum();
    }

    public long UpdateMany(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateExpression) =>
        UpdateManyAsync(filter, updateExpression).GetAwaiter().GetResult();

    /// <remarks>
    ///     A filter on the primary key alone, with an update CQL applies in place, is one <c>UPDATE</c> (one per chunk of a
    ///     long <c>IN</c>), counting the rows it found first (Cassandra's <c>UPDATE</c> is an upsert: a row deleted in between
    ///     is written again). A filter on other members too — or an update CQL cannot apply in place, such as <c>x.N + 1</c>
    ///     on a column that is not a counter — becomes a lightweight transaction per row: the rows the key finds are read,
    ///     and each is updated <c>IF</c> the other members still hold (and, for a value computed from the row, <c>IF</c> it
    ///     still holds what was read), one partition's transactions one at a time. The count is the updates Cassandra
    ///     applied. No update assigns the primary key or a store-generated column, whichever path it takes.
    /// </remarks>
    public async Task<long> UpdateManyAsync(Expression<Func<TEntity, bool>> filter,
        Expression<Func<TEntity, TEntity>> updateExpression, CancellationToken cancellationToken = default)
    {
        var assignments = Assignments(updateExpression);
        GuardAssignments(assignments, "An update");
        var prepared = await PrepareAsync(filter, "UPDATE", cancellationToken).ConfigureAwait(false);
        if (prepared.RowTests.Count > 0)
        {
            return (await UpdateConditionallyAsync(prepared.Split!, prepared.Filter, assignments, cancellationToken,
                prepared).ConfigureAwait(false)).Count;
        }

        filter = prepared.Filter;
        var split = CassandraConditions.Split(_configuration, filter, _unitOfWork.GlobalFilter<TEntity>());
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

            var count = await CountAsync(split, cancellationToken).ConfigureAwait(false);
            foreach (var (where, values) in split.KeyChunks)
            {
                await CassandraStatements.ExecuteAsync(_session,
                    $"UPDATE {_configuration.TableName} SET {set} WHERE {where}", [.. setValues, .. values], null,
                    _unitOfWork.CommandLogger, _unitOfWork.SensitiveLogging).ConfigureAwait(false);
            }

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
    ///     A set-based write's filter prepared: normalized, and each test across a relation resolved into the related keys,
    ///     read first. A write carries no opt-ins, so each related read is partition-scoped or refused: by the relation's own
    ///     condition (<c>x.Repo.WorkspaceId == w</c> reads the repositories of one workspace's partition), or — when the rest
    ///     of the filter names the write's partition — by the rows the write finds: the related table is read for their keys
    ///     by its partition key, in chunks (<c>x.Space == s &amp;&amp; !x.Runs.Any()</c> reads the runs of the items of space
    ///     <c>s</c>). Any other related read would read a whole table. A resolved test the key restriction or a lightweight
    ///     transaction's <c>IF</c> can hold (an <c>IN</c> on the key) stays in the filter, and the write finds its rows by it
    ///     as by any other. One they cannot hold — a negated relation, a <c>NOT IN</c> — is a <b>row test</b>: the rows the
    ///     rest of the filter finds (under the write's partition) are read, a row whose key the related read returned is left
    ///     out, and each other row is written by its full key in a lightweight transaction that also holds every column the
    ///     test read at the value read. A negated test resolved for the rows the write found holds only for those rows: one
    ///     the write meets later (inserted in between) is left alone, since its related rows were never read.
    /// </summary>
    private async Task<PreparedWrite> PrepareAsync(Expression<Func<TEntity, bool>> filter, string statement, CancellationToken cancellationToken)
    {
        var normalized = PredicateNormalizer.Normalize(filter);
        var parameter = normalized.Parameters[0];
        var navigations = CassandraPredicates.Navigations(_unitOfWork);
        var plain = new List<Expression>();
        var crossing = new List<Expression>();
        foreach (var conjunct in Conjuncts(normalized.Body))
        {
            var lambda = Expression.Lambda<Func<TEntity, bool>>(conjunct, parameter);
            (RelationPredicates.Find(lambda, navigations).Count == 0 ? plain : crossing).Add(conjunct);
        }

        if (crossing.Count == 0)
        {
            return new PreparedWrite(Expression.Lambda<Func<TEntity, bool>>(plain.Aggregate(Expression.AndAlso), parameter), null, [], []);
        }

        // The rows the rest of the filter finds, read once and only when a relation's read needs them as its scope.
        IReadOnlyList<global::Cassandra.Row>? candidates = null;
        async Task<IReadOnlyList<global::Cassandra.Row>?> CandidatesAsync()
        {
            if (candidates is not null || plain.Count == 0)
            {
                return candidates;
            }

            var split = CassandraConditions.Split(_configuration,
                Expression.Lambda<Func<TEntity, bool>>(plain.Aggregate(Expression.AndAlso), parameter), _unitOfWork.GlobalFilter<TEntity>());
            return split.PinsPartition ? candidates = await KeyedRowsAsync(split, cancellationToken).ConfigureAwait(false) : null;
        }

        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scopeTests = new List<Func<TEntity, bool>>();
        async Task<IReadOnlyCollection<object?>?> ScopeAsync(RelationPredicate relation)
        {
            if (relation.Navigation.Owner != typeof(TEntity) || await CandidatesAsync().ConfigureAwait(false) is not { } rows)
            {
                return null;
            }

            var ownerKey = relation.Navigation.OwnerKey;
            var scope = rows.Select(row => ownerKey.GetValue(CassandraMapper.Materialize<TEntity>(_configuration, row)))
                .Where(value => value is not null).Distinct().ToList();
            if (relation.Negated)
            {
                // No related row among the ones read for these keys says nothing of a key outside them.
                var known = new HashSet<string>(scope.Select(KeyText), StringComparer.Ordinal);
                scopeTests.Add(entity => ownerKey.GetValue(entity) is not { } value || known.Contains(KeyText(value)));
                var column = _configuration.ColumnFor(ownerKey.Name);
                if (!_configuration.IsKey(column) && _configuration.Column(column) is not null)
                {
                    held.Add(column);
                }
            }

            return scope;
        }

        var pushed = new List<Expression>(plain);
        var rowTests = new List<Expression>();
        foreach (var conjunct in crossing)
        {
            var resolved = await CassandraPredicates.ResolveForWriteAsync(Expression.Lambda<Func<TEntity, bool>>(conjunct, parameter),
                _unitOfWork, ScopeAsync, cancellationToken).ConfigureAwait(false);
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

        var keyed = CassandraConditions.Split(_configuration,
            Expression.Lambda<Func<TEntity, bool>>(pushed.Aggregate(Expression.AndAlso), parameter), _unitOfWork.GlobalFilter<TEntity>());
        RequireConditionalWrite(keyed, statement);
        new ColumnReader(parameter, member =>
        {
            var column = _configuration.ColumnFor(member);
            if (!_configuration.IsKey(column) && _configuration.Column(column) is not null)
            {
                held.Add(column);
            }
        }).Visit(Expression.Lambda(rowTests.Aggregate(Expression.AndAlso), parameter));
        return new PreparedWrite(whole, keyed,
            [.. rowTests.Select(test => Expression.Lambda<Func<TEntity, bool>>(test, parameter).Compile()), .. scopeTests], held.ToList());
    }

    /// <summary>A key compared by its text, whatever its CLR type.</summary>
    private static string KeyText(object? value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

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
        var deleted = await PerPartitionAsync(rows, async row =>
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
                await CassandraLightweightTransactions.BackoffAsync(attempt).ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                $"A row of '{_configuration.TableName}' kept changing while it was being deleted; {ConditionalAttempts} attempts did not settle.");
        }).ConfigureAwait(false);
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
        GuardAssignments(assignments, "An update");
        RequireConditionalWrite(split, "UPDATE");
        var global = _unitOfWork.GlobalFilter<TEntity>();
        var matches = (global is null ? filter : filter.AndAlso(global)).Compile();
        var keys = CassandraMapper.PrimaryKey(_configuration);
        var rows = await KeyedRowsAsync(split, cancellationToken).ConfigureAwait(false);

        var updated = await PerPartitionAsync(rows, async row =>
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

                // Not applied, or timed out with its outcome unknown: read the row again at the serial consistency. A
                // timed-out transaction counts only for what the read proves (see Settle). Then, when the row still matches,
                // compute again; otherwise it is not this update's.
                var fresh = await ReadRowAsync(row, keys).ConfigureAwait(false);
                var freshEntity = fresh is null ? null : CassandraMapper.Materialize<TEntity>(_configuration, fresh);
                if (timeout is not null)
                {
                    switch (Settle(freshEntity, current, next, assignments, bumpsVersion: false))
                    {
                        case Outcome.Applied:
                            return next;
                        case Outcome.Unknown:
                            throw Unsettled(timeout);
                    }
                }

                if (fresh is null || !matches(freshEntity!))
                {
                    return null;
                }

                row = fresh;
                await CassandraLightweightTransactions.BackoffAsync(attempt).ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                $"A row of '{_configuration.TableName}' kept changing while it was being updated; {ConditionalAttempts} attempts did not settle.");
        }).ConfigureAwait(false);

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
    ///     The rows the key restriction finds (a chunk of a long <c>IN</c> at a time), read at the serial consistency a
    ///     lightweight transaction writes at — or, when the restriction names several partitions (Cassandra takes a serial
    ///     read of one partition at a time), at the session's consistency: each row's own transaction still checks its
    ///     condition before it writes.
    /// </summary>
    private async Task<List<global::Cassandra.Row>> KeyedRowsAsync(CassandraConditions split, CancellationToken cancellationToken)
    {
        var found = new List<global::Cassandra.Row>();
        foreach (var (where, values) in split.KeyChunks)
        {
            var serial = true;
            for (var attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var cql = $"SELECT * FROM {_configuration.TableName} WHERE {where}";
                    var rows = serial
                        ? await CassandraStatements.ExecuteSerialAsync(_session, cql, values, _unitOfWork.CommandLogger,
                            _unitOfWork.SensitiveLogging).ConfigureAwait(false)
                        : await CassandraStatements.ExecuteAsync(_session, cql, values, null, _unitOfWork.CommandLogger,
                            _unitOfWork.SensitiveLogging).ConfigureAwait(false);
                    found.AddRange(rows);
                    break;
                }
                catch (InvalidQueryException exception) when (serial
                                                              && exception.Message.Contains("one partition", StringComparison.OrdinalIgnoreCase))
                {
                    serial = false;
                }
                catch (Exception exception) when (exception is ReadTimeoutException or WriteTimeoutException && attempt < ConditionalAttempts)
                {
                    // A serial read takes part in Paxos, and times out under the same contention: read again.
                    await CassandraLightweightTransactions.BackoffAsync(attempt).ConfigureAwait(false);
                }
            }
        }

        return found;
    }

    /// <summary>
    ///     Runs a lightweight transaction per row: one partition's rows one after the other, so no two of them contend in
    ///     its Paxos, and several partitions side by side (<see cref="CassandraLightweightTransactions.PerPartitionAsync{TItem,TResult}" />).
    /// </summary>
    private Task<TResult[]> PerPartitionAsync<TResult>(IReadOnlyList<global::Cassandra.Row> rows, Func<global::Cassandra.Row, Task<TResult>> run) =>
        CassandraLightweightTransactions.PerPartitionAsync(rows, row => CassandraMapper.PartitionText(_configuration, row), run);

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
                var rows = await CassandraStatements.ExecuteSerialAsync(_session,
                    $"SELECT * FROM {_configuration.TableName} WHERE {string.Join(" AND ", keys.Select(key => $"{key} = ?"))}",
                    keyValues, _unitOfWork.CommandLogger, _unitOfWork.SensitiveLogging).ConfigureAwait(false);
                return rows.FirstOrDefault();
            }
            catch (Exception exception) when (exception is ReadTimeoutException or WriteTimeoutException && attempt < ConditionalAttempts)
            {
                await CassandraLightweightTransactions.BackoffAsync(attempt).ConfigureAwait(false);
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
        RelationPredicates.RefuseInConflictFilter(conflictFilter, CassandraPredicates.Navigations(_unitOfWork));
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

        CassandraUnitOfWork.GuardCounters(typeof(TEntity), _configuration);
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
                inserted = (await CassandraStatements.ExecuteConditionalAsync(_session, insert + " IF NOT EXISTS", insertValues, null,
                    _unitOfWork.CommandLogger, _unitOfWork.SensitiveLogging).ConfigureAwait(false)).First();
            }
            catch (Exception exception) when (CassandraLightweightTransactions.IsTimedOut(exception))
            {
                // Timed out under contention, its outcome unknown: a serial read settles it. The row this insert wrote is
                // the entity's (its own values), and then the insert applied; any other row makes this the update.
                inserted = await ReadRowAsync(entity, keys).ConfigureAwait(false);
                if (inserted is null)
                {
                    await CassandraLightweightTransactions.BackoffAsync(attempt).ConfigureAwait(false);
                    continue;
                }

                if (CassandraMapper.HoldsInsert(_configuration, CassandraMapper.Materialize<TEntity>(_configuration, inserted), entity))
                {
                    CassandraMapper.AssignReadBack(_configuration, entity, inserted);
                    return entity;
                }
            }

            if (inserted.Length > 0 && inserted.GetColumn("[applied]") is not null && inserted.GetValue<bool>("[applied]"))
            {
                // The clock the coordinator filled, read at the serial consistency the insert ran at.
                if (CassandraMapper.BuildReadBack(_configuration, entity) is var (readBack, readBackValues))
                {
                    var read = await CassandraStatements.ExecuteSerialAsync(_session, readBack, readBackValues,
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

            await CassandraLightweightTransactions.BackoffAsync(attempt).ConfigureAwait(false);
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

        GuardAssignments(assignments, "An upsert's update");
        foreach (var assignment in assignments)
        {
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
        if (timeout is null)
        {
            return applied == true ? next : null;
        }

        // Timed out, its outcome unknown: the serial read decides, as far as it proves anything (see Settle). A row that is
        // gone sends the upsert back to its insert.
        if (await ReadRowAsync(row, keys).ConfigureAwait(false) is not { } settled)
        {
            return null;
        }

        return Settle(CassandraMapper.Materialize<TEntity>(_configuration, settled), existing, next, assignments,
                bumpsVersion: _configuration.ConcurrencyColumn is not null) switch
            {
                Outcome.Applied => next,
                Outcome.NotApplied => null,
                _ => throw Unsettled(timeout),
            };
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
            var result = await CassandraStatements.ExecuteConditionalAsync(_session, cql, values, null, _unitOfWork.CommandLogger,
                _unitOfWork.SensitiveLogging).ConfigureAwait(false);
            return (result.First().GetValue<bool>("[applied]"), null);
        }
        catch (WriteTimeoutException timeout) when (CassandraLightweightTransactions.IsTimedOut(timeout))
        {
            return (null, timeout);
        }
    }

    /// <summary>What the serial read after a timed-out update proves about it.</summary>
    private enum Outcome
    {
        /// <summary>The row holds what the update writes: it applied, or one with the same effect did.</summary>
        Applied,

        /// <summary>It did not apply, and the write may be decided again from the row read.</summary>
        NotApplied,

        /// <summary>Nothing the read shows tells whether it applied.</summary>
        Unknown,
    }

    /// <summary>
    ///     What a row read at the serial consistency after an update timed out proves (<paramref name="stored" />, <c>null</c>
    ///     when the row is gone; <paramref name="read" /> is the row the update was computed from, <paramref name="next" />
    ///     what it writes). The serial read completes or abandons for good the round the update left, so the row is what the
    ///     transactions decided.
    ///     <list type="bullet">
    ///         <item>
    ///             An update of values set outright: the row holding them is this update's effect (it applied, or one with the
    ///             same effect did); otherwise it did not land, and it is written again while the row matches.
    ///         </item>
    ///         <item>
    ///             A value computed from the row (<c>x.N + 1</c>, a bumped version) cannot tell this update's write from
    ///             another's of the same sum, nor a later write over it from no write at all — a value can come back to what
    ///             it was. Only a version the update conditions on and bumps proves anything: versions only grow, so a row
    ///             still at the version read was not written by it, and the update is decided again. Anything else is
    ///             <see cref="Outcome.Unknown" />, and its timeout reaches the caller (<see cref="Unsettled" />).
    ///         </item>
    ///     </list>
    /// </summary>
    private Outcome Settle(TEntity? stored, TEntity read, TEntity next, IReadOnlyList<UpdateAssignment> assignments, bool bumpsVersion)
    {
        if (!bumpsVersion && assignments.All(assignment => assignment is SetAssignment))
        {
            return stored is not null && assignments.All(assignment =>
                CassandraMapper.Same(Member(stored, assignment.Name), Member(next, assignment.Name)))
                ? Outcome.Applied
                : Outcome.NotApplied;
        }

        if (bumpsVersion && stored is not null && _configuration.ConcurrencyColumn is { } token
            && Convert.ToInt64(Member(stored, _configuration.MemberFor(token)) ?? 0L) == Convert.ToInt64(Member(read, _configuration.MemberFor(token)) ?? 0L))
        {
            return Outcome.NotApplied;
        }

        return Outcome.Unknown;
    }

    /// <summary>The timeout of a transaction whose outcome no read can prove, for the caller to decide.</summary>
    private TimeoutException Unsettled(WriteTimeoutException timeout) => new(
        $"A lightweight transaction on '{_configuration.TableName}' timed out under contention, and Cassandra cannot say " +
        "whether it applied: a value it computed from the row (an increment, the version) is not written a second time. " +
        "Read the row and decide.", timeout);

    /// <summary>Refuses an update that assigns the primary key (it identifies the row) or a store-generated column (the store assigns it).</summary>
    private void GuardAssignments(IEnumerable<UpdateAssignment> assignments, string what)
    {
        foreach (var assignment in assignments)
        {
            var column = _configuration.ColumnFor(assignment.Name);
            if (_configuration.IsKey(column) || _configuration.IsStoreGenerated(column))
            {
                throw new NotSupportedException(
                    $"{what} cannot assign '{assignment.Name}': the primary key identifies the row, and the store assigns a store-generated column.");
            }
        }
    }

    // ---------------------------------------------------------------- inserts skipping duplicates (immediate)

    /// <summary>
    ///     Inserts every entity with a lightweight transaction (<c>INSERT … IF NOT EXISTS</c>), one partition's one at a time,
    ///     and counts the inserts Cassandra applied; every number their sequences hand out is reserved first, in one
    ///     compare-and-set per sequence. A primary key already stored is not applied: with
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
        var applied = await _unitOfWork.InsertIfNotExistsAsync(entities).ConfigureAwait(false);
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

    /// <summary>The rows the key restriction finds, counted on the cluster (a chunk of a long <c>IN</c> at a time).</summary>
    private async Task<long> CountAsync(CassandraConditions split, CancellationToken cancellationToken)
    {
        var count = 0L;
        foreach (var (where, values) in split.KeyChunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cql = $"SELECT COUNT(*) FROM {_configuration.TableName}" + (where.Length > 0 ? $" WHERE {where}" : string.Empty);
            var rows = await CassandraStatements.ExecuteAsync(_session, cql, values, null, _unitOfWork.CommandLogger, _unitOfWork.SensitiveLogging)
                .ConfigureAwait(false);
            count += rows.First().GetValue<long>(0);
        }

        return count;
    }
}
