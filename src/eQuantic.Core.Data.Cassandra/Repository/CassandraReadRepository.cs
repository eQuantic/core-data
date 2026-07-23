using System.Diagnostics;
using System.Linq.Expressions;
using eQuantic.Core.Data.Diagnostics;
using eQuantic.Core.Data.Query;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Read;
using eQuantic.Linq.Expressions;
using eQuantic.Linq.Specification;
using eQuantic.Linq.Web;
using global::Cassandra;

namespace eQuantic.Core.Data.Cassandra.Repository;

/// <summary>
///     The native Apache Cassandra read repository. A <see cref="QueryOptions{TEntity}" /> filter is split by the
///     pushdown engine: every clause CQL can express runs on the cluster (equality/IN/ranges over the keys,
///     <c>token()</c> partition ranges, <c>CONTAINS</c>), and the clauses it cannot (<c>OR</c> across columns,
///     <c>!=</c>, <c>NULL</c>, arbitrary predicates) run client-side over the fetched rows — behind the explicit
///     <c>.AllowClientEvaluation()</c> opt-in, with <c>.AllowFiltering()</c> gating scans, exactly as
///     <see cref="Explain" /> reports. Statements are prepared once per session; aggregate <c>Sum</c>s and
///     projections push down when the selector allows. Sorting is limited to clustering keys, and paging fetches
///     the first <c>skip+take</c> rows then slices client-side (Cassandra has no OFFSET). Synchronous members
///     delegate to the asynchronous ones.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TKey">The key type.</typeparam>
public abstract class CassandraReadRepository<TEntity, TKey> :
    IQueryableReadRepository<TEntity, TKey>,
    IAsyncQueryableReadRepository<TEntity, TKey>,
    IExplainableRepository<TEntity>,
    IContinuationReadRepository<TEntity>,
    IStreamingReadRepository<TEntity>,
    IAggregateReadRepository<TEntity>,
    IGroupedReadRepository<TEntity>
    where TEntity : class, IEntity<TKey>
{
    /// <summary>The unit of work backing this repository.</summary>
    protected readonly CassandraUnitOfWork UnitOfWork;

    /// <summary>The session.</summary>
    protected readonly ISession Session;

    private readonly CassandraEntityConfiguration _configuration;
    private readonly LambdaExpression _keySelector;

    /// <summary>Initializes the repository over a unit of work.</summary>
    /// <param name="unitOfWork">The queryable unit of work (a <see cref="CassandraUnitOfWork" />).</param>
    protected CassandraReadRepository(IQueryableUnitOfWork unitOfWork)
    {
        UnitOfWork = unitOfWork as CassandraUnitOfWork
                     ?? throw new ArgumentException($"The unit of work must be a {nameof(CassandraUnitOfWork)}.", nameof(unitOfWork));
        Session = UnitOfWork.GetSession();
        _configuration = UnitOfWork.Configuration<TEntity>();
        _keySelector = MemberPathExtensions.ToSelector<TEntity>(_configuration.MemberFor(_configuration.KeyColumn));
    }

    // ---------------------------------------------------------------- asynchronous reads

    /// <inheritdoc />
    public async Task<TEntity?> GetAsync(TKey id, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        (await SelectAsync(options, 1, cancellationToken, IdPredicate(id)).ConfigureAwait(false)).FirstOrDefault();

    /// <inheritdoc />
    public async Task<IEnumerable<TEntity>> GetAllAsync(QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await SelectAsync(options, null, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IEnumerable<TEntity>> GetFilteredAsync(Expression<Func<TEntity, bool>> filter, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await SelectAsync(options, null, cancellationToken, NotNull(filter)).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IEnumerable<TEntity>> AllMatchingAsync(ISpecification<TEntity> specification, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await SelectAsync(options, null, cancellationToken, NotNull(specification).SatisfiedBy()).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IEnumerable<TResult>> GetMappedAsync<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        (await SelectAsync(options, null, cancellationToken, null, MapColumns(NotNull(map))).ConfigureAwait(false)).Select(map.Compile()).ToList();

    /// <inheritdoc />
    public async Task<TEntity?> GetFirstAsync(QueryOptions<TEntity> options, CancellationToken cancellationToken = default) =>
        (await SelectAsync(options, 1, cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    /// <inheritdoc />
    public async Task<TResult?> GetFirstMappedAsync<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity> options, CancellationToken cancellationToken = default) =>
        (await SelectAsync(options, 1, cancellationToken, null, MapColumns(NotNull(map))).ConfigureAwait(false)).Select(map.Compile()).FirstOrDefault();

    /// <inheritdoc />
    public async Task<TEntity?> GetSingleAsync(QueryOptions<TEntity> options, CancellationToken cancellationToken = default) =>
        (await SelectAsync(options, 2, cancellationToken).ConfigureAwait(false)).SingleOrDefault();

    /// <inheritdoc />
    public async Task<PagedResult<TEntity>> GetPagedAsync(PageRequest page, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        var total = await CountAsync(options, cancellationToken).ConfigureAwait(false);
        var rows = await SelectAsync(options, page.Skip + page.Take, cancellationToken).ConfigureAwait(false);
        return new PagedResult<TEntity>(rows.Skip(page.Skip).Take(page.Take).ToList(), total, page.PageIndex, page.PageSize);
    }

    /// <inheritdoc />
    public async Task<PagedResult<TResult>> GetPagedAsync<TResult>(PageRequest page, Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        var total = await CountAsync(options, cancellationToken).ConfigureAwait(false);
        var rows = await SelectAsync(options, page.Skip + page.Take, cancellationToken, null, MapColumns(NotNull(map))).ConfigureAwait(false);
        var items = rows.Skip(page.Skip).Take(page.Take).Select(map.Compile()).ToList();
        return new PagedResult<TResult>(items, total, page.PageIndex, page.PageSize);
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        var plan = GatedPlan(options, null);

        if (plan.Residual.Count == 0 && plan.Alternatives.Count == 0)
        {
            var cql = $"SELECT COUNT(*) FROM {_configuration.TableName}"
                      + (plan.Where.Length > 0 ? $" WHERE {plan.Where}" : string.Empty)
                      + (plan.RequiresAllowFiltering ? " ALLOW FILTERING" : string.Empty);
            var rows = await CassandraStatements.ExecuteAsync(Session, cql, plan.Values,
                CassandraQueryOptionsExtensions.ConsistencyOf(options), UnitOfWork.CommandLogger, UnitOfWork.SensitiveLogging).ConfigureAwait(false);
            return rows.First().GetValue<long>(0);
        }

        // Residual/split: fetch only the needed columns, de-duplicate and filter client-side, and count.
        return (await SelectAsync(options, null, cancellationToken, null, []).ConfigureAwait(false)).Count;
    }

    /// <inheritdoc />
    public async Task<bool> AnyAsync(QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        (await SelectAsync(options, 1, cancellationToken).ConfigureAwait(false)).Count > 0;

    /// <inheritdoc />
    public async Task<bool> AllAsync(Expression<Func<TEntity, bool>> predicate, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        (await SelectAsync(options, null, cancellationToken).ConfigureAwait(false)).All(predicate.Compile());

    /// <inheritdoc />
    public Task<int> SumAsync(Expression<Func<TEntity, int>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        SumCoreAsync(selector, options, cancellationToken, rows => rows.Sum(selector.Compile()));

    /// <inheritdoc />
    public Task<int?> SumAsync(Expression<Func<TEntity, int?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        SumCoreAsync(selector, options, cancellationToken, rows => rows.Sum(selector.Compile()));

    /// <inheritdoc />
    public Task<long> SumAsync(Expression<Func<TEntity, long>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        SumCoreAsync(selector, options, cancellationToken, rows => rows.Sum(selector.Compile()));

    /// <inheritdoc />
    public Task<long?> SumAsync(Expression<Func<TEntity, long?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        SumCoreAsync(selector, options, cancellationToken, rows => rows.Sum(selector.Compile()));

    /// <inheritdoc />
    public Task<double> SumAsync(Expression<Func<TEntity, double>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        SumCoreAsync(selector, options, cancellationToken, rows => rows.Sum(selector.Compile()));

    /// <inheritdoc />
    public Task<double?> SumAsync(Expression<Func<TEntity, double?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        SumCoreAsync(selector, options, cancellationToken, rows => rows.Sum(selector.Compile()));

    /// <inheritdoc />
    public Task<float> SumAsync(Expression<Func<TEntity, float>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        SumCoreAsync(selector, options, cancellationToken, rows => rows.Sum(selector.Compile()));

    /// <inheritdoc />
    public Task<float?> SumAsync(Expression<Func<TEntity, float?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        SumCoreAsync(selector, options, cancellationToken, rows => rows.Sum(selector.Compile()));

    /// <inheritdoc />
    public Task<decimal> SumAsync(Expression<Func<TEntity, decimal>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        SumCoreAsync(selector, options, cancellationToken, rows => rows.Sum(selector.Compile()));

    /// <inheritdoc />
    public Task<decimal?> SumAsync(Expression<Func<TEntity, decimal?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        SumCoreAsync(selector, options, cancellationToken, rows => rows.Sum(selector.Compile()));

    // ---------------------------------------------------------------- min / max / average

    /// <inheritdoc />
    public async Task<TResult?> MinAsync<TResult>(Expression<Func<TEntity, TResult>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await CqlAggregateAsync(selector, options, cancellationToken,
            column => $"MIN({column})", row => row.GetValue<TResult?>(0),
            rows => rows.Count == 0 ? default : rows.Min(selector.Compile())).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<TResult?> MaxAsync<TResult>(Expression<Func<TEntity, TResult>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await CqlAggregateAsync(selector, options, cancellationToken,
            column => $"MAX({column})", row => row.GetValue<TResult?>(0),
            rows => rows.Count == 0 ? default : rows.Max(selector.Compile())).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<double> AverageAsync<TValue>(Expression<Func<TEntity, TValue>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await CqlAggregateAsync(selector, options, cancellationToken,
            column => $"AVG(CAST({column} AS double))", row => row.GetValue<double>(0),
            rows => rows.Count == 0 ? 0d : rows.Average(row => Convert.ToDouble(selector.Compile()(row)))).ConfigureAwait(false);

    private async Task<TResult> CqlAggregateAsync<TSelected, TResult>(Expression<Func<TEntity, TSelected>> selector,
        QueryOptions<TEntity>? options, CancellationToken cancellationToken,
        Func<string, string> aggregate, Func<Row, TResult> read, Func<List<TEntity>, TResult> clientFallback)
    {
        var column = SumColumn(selector);
        if (column is not null)
        {
            var plan = GatedPlan(options, null);
            if (plan.Residual.Count == 0 && plan.Alternatives.Count == 0)
            {
                var cql = $"SELECT {aggregate(column)} FROM {_configuration.TableName}"
                          + (plan.Where.Length > 0 ? $" WHERE {plan.Where}" : string.Empty)
                          + (plan.RequiresAllowFiltering ? " ALLOW FILTERING" : string.Empty);
                var row = (await CassandraStatements.ExecuteAsync(Session, cql, plan.Values,
                    CassandraQueryOptionsExtensions.ConsistencyOf(options), UnitOfWork.CommandLogger, UnitOfWork.SensitiveLogging).ConfigureAwait(false)).First();
                return row.IsNull(0) ? default! : read(row);
            }
        }

        return clientFallback(await SelectAsync(options, null, cancellationToken).ConfigureAwait(false));
    }

    // ---------------------------------------------------------------- grouped reads

    /// <inheritdoc />
    /// <remarks>
    ///     Renders to a native CQL <c>GROUP BY</c> — restricted, as Cassandra requires, to the primary key: the
    ///     key must be the full partition key, optionally followed by clustering columns in order. The filter
    ///     pushes into the <c>WHERE</c> under the usual gates, and a residual filter degrades — behind the same
    ///     opt-ins as any read — to fetching the matching rows and grouping them with the selectors themselves.
    ///     CQL has no <c>HAVING</c>: the predicate's aggregates are computed <b>on the cluster</b> as extra
    ///     select columns and the groups are filtered as they stream back — no extra rows travel either way.
    /// </remarks>
    public async Task<IReadOnlyList<TResult>> GroupByAsync<TGroup, TResult>(
        Expression<Func<TEntity, TGroup>> keySelector,
        Expression<Func<IGrouping<TGroup, TEntity>, TResult>> resultSelector,
        Expression<Func<IGrouping<TGroup, TEntity>, bool>>? having = null,
        QueryOptions<TEntity>? options = null,
        CancellationToken cancellationToken = default)
    {
        if (options is { Sortings.Count: > 0 })
        {
            throw new NotSupportedException("Sorting does not apply to a grouped read; order the grouped result client-side.");
        }

        // Interpret first so the contract is uniform across providers: the same shapes, the same rejections.
        var group = GroupInterpreter.Interpret(NotNull(keySelector), NotNull(resultSelector));
        var predicate = having is null ? null : GroupInterpreter.InterpretHaving(having, group.Key);
        var plan = GatedPlan(options, null);

        if (plan.Alternatives.Count > 0)
        {
            throw new NotSupportedException(
                "An OR-split query cannot group on the cluster (per-branch groups would need re-aggregation); " +
                "restructure the filter, or group client-side over separate reads.");
        }

        if (plan.Residual.Count > 0)
        {
            var fetched = await SelectAsync(options, null, cancellationToken).ConfigureAwait(false);
            var grouped = fetched.GroupBy(keySelector.Compile());
            if (having is not null)
            {
                grouped = grouped.Where(having.Compile());
            }

            return grouped.Select(resultSelector.Compile()).ToList();
        }

        var keys = group.Key.Select(member => ColumnNamed(member.Path, "group by")).ToList();
        ValidateGroupKeys(keys);

        // The aggregate cells: the projected ones first, then any the HAVING needs that are not already projected.
        var cells = group.Bindings.OfType<GroupAggregateBinding>()
            .Select(binding => (binding.Aggregate, binding.Member))
            .ToList();
        if (predicate is not null)
        {
            foreach (var comparison in Comparisons(predicate))
            {
                if (comparison.Aggregate is { } aggregate && CellIndex(cells, aggregate, comparison.Member) < 0)
                {
                    cells.Add((aggregate, comparison.Member));
                }
            }
        }

        var select = keys.Concat(cells.Select(cell => AggregateCql(cell.Item1, cell.Item2))).ToList();
        var cql = $"SELECT {string.Join(", ", select)} FROM {_configuration.TableName}"
                  + (plan.Where.Length > 0 ? $" WHERE {plan.Where}" : string.Empty)
                  + $" GROUP BY {string.Join(", ", keys)}"
                  + (plan.RequiresAllowFiltering ? " ALLOW FILTERING" : string.Empty);

        using var activity = DataActivitySource.Instance.StartActivity("cassandra.group_by", ActivityKind.Client);
        if (activity is not null)
        {
            activity.SetTag("db.system", "cassandra");
            activity.SetTag("equantic.partition_scoped", plan.PartitionScoped);
            activity.SetTag("equantic.allow_filtering", plan.RequiresAllowFiltering);
        }

        var rows = await CassandraStatements.ExecuteAsync(Session, cql, plan.Values,
            CassandraQueryOptionsExtensions.ConsistencyOf(options), UnitOfWork.CommandLogger, UnitOfWork.SensitiveLogging).ConfigureAwait(false);

        var constructor = group.ConstructorProjection ? typeof(TResult).GetConstructors().Single() : null;
        var properties = group.ConstructorProjection
            ? null
            : group.Bindings.Select(binding => typeof(TResult).GetProperty(binding.Target)
                                               ?? throw new NotSupportedException(
                                                   $"'{typeof(TResult).Name}' has no member '{binding.Target}'.")).ToArray();
        var targets = constructor?.GetParameters().Select(parameter => parameter.ParameterType).ToArray()
                      ?? properties!.Select(property => property.PropertyType).ToArray();
        var keyConstructor = group.Key is [{ Name: not null }, ..] ? typeof(TGroup).GetConstructors().Single() : null;

        var results = new List<TResult>();
        foreach (var row in rows)
        {
            var keyValues = new object?[keys.Count];
            for (var ordinal = 0; ordinal < keyValues.Length; ordinal++)
            {
                keyValues[ordinal] = row.IsNull(ordinal) ? null : row.GetValue<object>(ordinal);
            }

            var cellValues = new object?[cells.Count];
            for (var ordinal = 0; ordinal < cellValues.Length; ordinal++)
            {
                cellValues[ordinal] = row.IsNull(keys.Count + ordinal) ? null : row.GetValue<object>(keys.Count + ordinal);
            }

            if (predicate is not null && !EvaluateHaving(predicate, group, cells, keyValues, cellValues))
            {
                continue;
            }

            var values = new object?[group.Bindings.Count];
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = group.Bindings[index] switch
                {
                    GroupKeyBinding { KeyName: null } when keyConstructor is null =>
                        ChangeValue(keyValues[0], typeof(TGroup)),
                    GroupKeyBinding { KeyName: null } => keyConstructor.Invoke(keyConstructor.GetParameters()
                        .Select((parameter, position) => ChangeValue(keyValues[position], parameter.ParameterType))
                        .ToArray()),
                    GroupKeyBinding named => ChangeValue(
                        keyValues[KeyOrdinal(group, named.KeyName!)], targets[index]),
                    GroupAggregateBinding aggregate => ChangeValue(
                        cellValues[CellIndex(cells, aggregate.Aggregate, aggregate.Member)], targets[index]),
                    var unknown => throw new NotSupportedException($"Unknown group binding '{unknown.GetType().Name}'."),
                };
            }

            if (constructor is not null)
            {
                results.Add((TResult)constructor.Invoke(values));
            }
            else
            {
                var result = Activator.CreateInstance<TResult>()!;
                for (var index = 0; index < values.Length; index++)
                {
                    properties![index].SetValue(result, values[index]);
                }

                results.Add(result);
            }
        }

        return results;
    }

    private string ColumnNamed(string path, string purpose) =>
        _configuration.Columns.FirstOrDefault(column => CassandraEntityConfiguration.Same(column.Member, path))?.Name
        ?? throw new NotSupportedException($"'{typeof(TEntity).Name}' has no mapped column '{path}' to {purpose}.");

    /// <summary>CQL groups by the primary key only: the full partition key, then clustering columns in order.</summary>
    private void ValidateGroupKeys(IReadOnlyList<string> keys)
    {
        var primary = _configuration.PartitionKeys
            .Concat(_configuration.ClusteringKeys.Select(key => key.Column))
            .ToList();
        var valid = keys.Count >= _configuration.PartitionKeys.Count
                    && keys.Count <= primary.Count
                    && keys.Select((key, index) => CassandraEntityConfiguration.Same(key, primary[index])).All(match => match);
        if (!valid)
        {
            throw new NotSupportedException(
                $"Cassandra groups by the primary key only: the key must be the full partition key ({string.Join(", ", _configuration.PartitionKeys)})" +
                (_configuration.ClusteringKeys.Count > 0
                    ? $", optionally followed by clustering columns in order ({string.Join(", ", _configuration.ClusteringKeys.Select(key => key.Column))})."
                    : "."));
        }
    }

    private string AggregateCql(GroupAggregate aggregate, string? member) => aggregate switch
    {
        GroupAggregate.Count => "COUNT(*)",
        GroupAggregate.Sum => $"SUM({ColumnNamed(member!, "aggregate")})",
        GroupAggregate.Min => $"MIN({ColumnNamed(member!, "aggregate")})",
        GroupAggregate.Max => $"MAX({ColumnNamed(member!, "aggregate")})",
        _ => $"AVG(CAST({ColumnNamed(member!, "aggregate")} AS double))",
    };

    private static int CellIndex(List<(GroupAggregate Aggregate, string? Member)> cells, GroupAggregate aggregate, string? member)
    {
        for (var index = 0; index < cells.Count; index++)
        {
            if (cells[index].Aggregate == aggregate
                && (cells[index].Member is null
                    ? member is null
                    : member is not null && CassandraEntityConfiguration.Same(cells[index].Member!, member)))
            {
                return index;
            }
        }

        return -1;
    }

    private static int KeyOrdinal(GroupQuery group, string keyName)
    {
        for (var index = 0; index < group.Key.Count; index++)
        {
            if (string.Equals(group.Key[index].Name, keyName, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        throw new NotSupportedException($"The key has no member '{keyName}'.");
    }

    /// <summary>Evaluates the interpreted HAVING against one grouped row's key and aggregate cells.</summary>
    private static bool EvaluateHaving(GroupPredicate predicate, GroupQuery group,
        List<(GroupAggregate Aggregate, string? Member)> cells, object?[] keyValues, object?[] cellValues) => predicate switch
    {
        GroupLogical { Operator: LogicalOperator.And } logical =>
            logical.Operands.All(operand => EvaluateHaving(operand, group, cells, keyValues, cellValues)),
        GroupLogical logical =>
            logical.Operands.Any(operand => EvaluateHaving(operand, group, cells, keyValues, cellValues)),
        GroupComparison comparison => CompareHaving(comparison, comparison.Aggregate is { } aggregate
            ? cellValues[CellIndex(cells, aggregate, comparison.Member)]
            : keyValues[KeyPathOrdinal(group, comparison.Member!)]),
        _ => throw new NotSupportedException($"Unknown group predicate '{predicate.GetType().Name}'."),
    };

    private static int KeyPathOrdinal(GroupQuery group, string path)
    {
        for (var index = 0; index < group.Key.Count; index++)
        {
            if (CassandraEntityConfiguration.Same(group.Key[index].Path, path))
            {
                return index;
            }
        }

        throw new NotSupportedException($"The key has no member '{path}'.");
    }

    private static bool CompareHaving(GroupComparison comparison, object? value)
    {
        if (comparison.Value is null)
        {
            return comparison.Operator switch
            {
                ComparisonOperator.Equal => value is null,
                ComparisonOperator.NotEqual => value is not null,
                _ => throw new NotSupportedException("Only == null and != null compare a group value against null."),
            };
        }

        if (value is null)
        {
            return false;
        }

        var aligned = (IComparable)ChangeValue(value, comparison.Value.GetType())!;
        var result = aligned.CompareTo(comparison.Value);
        return comparison.Operator switch
        {
            ComparisonOperator.Equal => result == 0,
            ComparisonOperator.NotEqual => result != 0,
            ComparisonOperator.GreaterThan => result > 0,
            ComparisonOperator.GreaterThanOrEqual => result >= 0,
            ComparisonOperator.LessThan => result < 0,
            _ => result <= 0,
        };
    }

    private static IEnumerable<GroupComparison> Comparisons(GroupPredicate predicate) => predicate switch
    {
        GroupComparison comparison => [comparison],
        GroupLogical logical => logical.Operands.SelectMany(Comparisons),
        _ => [],
    };

    private static object? ChangeValue(object? value, Type target)
    {
        if (value is null)
        {
            return null;
        }

        var type = Nullable.GetUnderlyingType(target) ?? target;
        if (type.IsInstanceOfType(value))
        {
            return value;
        }

        // The driver hands timestamps back as DateTimeOffset; entity members are usually DateTime.
        if (value is DateTimeOffset offset && type == typeof(DateTime))
        {
            return offset.UtcDateTime;
        }

        return type.IsEnum ? Enum.ToObject(type, value) : Convert.ChangeType(value, type);
    }

    // ---------------------------------------------------------------- continuation paging

    /// <inheritdoc />
    public async Task<ContinuedResult<TEntity>> GetPageAsync(int pageSize, string? continuationToken = null,
        QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "The page size must be at least 1.");
        }

        var plan = GatedPlan(options, null);
        if (plan.Alternatives.Count > 0)
        {
            throw new NotSupportedException(
                "Token paging cannot span an OR-split query (there is one paging state per statement); page each branch separately or restructure the filter.");
        }

        var residuals = Compile(plan.Residual);

        // The driver walks its native paging: one page per call, resumed by the (opaque) paging state.
        var bound = await CassandraStatements.BindAsync(Session,
            SelectCql(null, plan.Where, plan.RequiresAllowFiltering, options, pushLimit: false), plan.Values).ConfigureAwait(false);
        if (CassandraQueryOptionsExtensions.ConsistencyOf(options) is { } consistency)
        {
            bound.SetConsistencyLevel(consistency);
        }

        bound.SetPageSize(pageSize);
        bound.SetAutoPage(false);
        if (continuationToken is not null)
        {
            bound.SetPagingState(Convert.FromBase64String(continuationToken));
        }

        var rows = await Session.ExecuteAsync(bound).ConfigureAwait(false);
        var state = rows.PagingState;

        IEnumerable<TEntity> entities = rows.Select(row => CassandraMapper.Materialize<TEntity>(_configuration, row));
        if (residuals.Count > 0)
        {
            entities = entities.Where(entity => residuals.All(residual => residual(entity)));
        }

        return new ContinuedResult<TEntity>(entities.ToList(), state is null ? null : Convert.ToBase64String(state));
    }

    // ---------------------------------------------------------------- streaming

    /// <inheritdoc />
    public async IAsyncEnumerable<TEntity> GetStreamAsync(QueryOptions<TEntity>? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Streams over the native paging state: one page in memory at a time, stop whenever the consumer stops.
        string? token = null;
        do
        {
            var page = await GetPageAsync(1000, token, options, cancellationToken).ConfigureAwait(false);
            foreach (var entity in page.Items)
            {
                yield return entity;
            }

            token = page.ContinuationToken;
        } while (token is not null);
    }

    // ---------------------------------------------------------------- explain

    /// <inheritdoc />
    public QueryPlan Explain(QueryOptions<TEntity>? options = null)
    {
        var notes = new List<string>();
        if (options is { IncludePaths.Count: > 0 })
        {
            notes.Add("Include is not supported: Cassandra rows are self-contained (execution throws NotSupportedException).");
        }

        var global = GlobalFilter(options);
        if (global is not null)
        {
            notes.Add("A global query filter is ANDed into this query; IgnoringQueryFilters() opts out.");
        }

        var plan = CassandraCql.Plan(_configuration, options, null, global);

        string orderBy;
        try
        {
            orderBy = OrderBy(options);
        }
        catch (NotSupportedException exception)
        {
            orderBy = string.Empty;
            notes.Add(exception.Message);
        }

        var cql = $"SELECT * FROM {_configuration.TableName}"
                  + (plan.Where.Length > 0 ? $" WHERE {plan.Where}" : string.Empty)
                  + orderBy
                  + (plan.RequiresAllowFiltering ? " ALLOW FILTERING" : string.Empty);

        if (plan.RequiresAllowFiltering && !CassandraCql.AllowFilteringOptedIn(options))
        {
            notes.Add("Requires .AllowFiltering(): a pushed clause filters outside the primary key (server-side scan).");
        }

        if (plan.Residual.Count > 0)
        {
            if (!CassandraCql.ClientEvaluationOptedIn(options))
            {
                notes.Add("Requires .AllowClientEvaluation(): part of the filter cannot be expressed in CQL and runs client-side.");
            }

            if (!plan.PartitionScoped && !CassandraCql.AllowFilteringOptedIn(options))
            {
                notes.Add("Requires .AllowFiltering() as well: the residual filter is not partition-scoped, so the fetch scans the table.");
            }

            notes.Add("Rows are fetched without a CQL LIMIT and filtered client-side; LIMIT/Take applies after the residual.");
        }

        if (plan.Alternatives.Count > 0)
        {
            notes.Add($"Runs as {plan.Alternatives.Count} parallel single-partition queries (OR branches: "
                      + string.Join(" | ", plan.Alternatives.Select(alternative => alternative.Where))
                      + "), merged and de-duplicated by primary key client-side.");
        }

        notes.Add("The statement is prepared once per session and bound on every execution.");

        return new QueryPlan(
            "Cassandra",
            cql,
            plan.Values,
            plan.Residual.Count > 0 ? plan.ResidualText : null,
            plan.RequiresAllowFiltering,
            plan.Residual.Count > 0,
            plan.PartitionScoped,
            notes);
    }

    // ---------------------------------------------------------------- synchronous reads (delegate)

    /// <inheritdoc />
    public TEntity? Get(TKey id, QueryOptions<TEntity>? options = null) => GetAsync(id, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public IEnumerable<TEntity> GetAll(QueryOptions<TEntity>? options = null) => GetAllAsync(options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public IEnumerable<TEntity> GetFiltered(Expression<Func<TEntity, bool>> filter, QueryOptions<TEntity>? options = null) => GetFilteredAsync(filter, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public IEnumerable<TEntity> AllMatching(ISpecification<TEntity> specification, QueryOptions<TEntity>? options = null) => AllMatchingAsync(specification, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public IEnumerable<TResult> GetMapped<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null) => GetMappedAsync(map, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public TEntity? GetFirst(QueryOptions<TEntity> options) => GetFirstAsync(options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public TResult? GetFirstMapped<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity> options) => GetFirstMappedAsync(map, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public TEntity? GetSingle(QueryOptions<TEntity> options) => GetSingleAsync(options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public PagedResult<TEntity> GetPaged(PageRequest page, QueryOptions<TEntity>? options = null) => GetPagedAsync(page, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public PagedResult<TResult> GetPaged<TResult>(PageRequest page, Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null) => GetPagedAsync(page, map, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public long Count(QueryOptions<TEntity>? options = null) => CountAsync(options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public bool Any(QueryOptions<TEntity>? options = null) => AnyAsync(options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public bool All(Expression<Func<TEntity, bool>> predicate, QueryOptions<TEntity>? options = null) => AllAsync(predicate, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public int Sum(Expression<Func<TEntity, int>> selector, QueryOptions<TEntity>? options = null) => SumAsync(selector, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public int? Sum(Expression<Func<TEntity, int?>> selector, QueryOptions<TEntity>? options = null) => SumAsync(selector, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public long Sum(Expression<Func<TEntity, long>> selector, QueryOptions<TEntity>? options = null) => SumAsync(selector, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public long? Sum(Expression<Func<TEntity, long?>> selector, QueryOptions<TEntity>? options = null) => SumAsync(selector, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public double Sum(Expression<Func<TEntity, double>> selector, QueryOptions<TEntity>? options = null) => SumAsync(selector, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public double? Sum(Expression<Func<TEntity, double?>> selector, QueryOptions<TEntity>? options = null) => SumAsync(selector, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public float Sum(Expression<Func<TEntity, float>> selector, QueryOptions<TEntity>? options = null) => SumAsync(selector, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public float? Sum(Expression<Func<TEntity, float?>> selector, QueryOptions<TEntity>? options = null) => SumAsync(selector, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public decimal Sum(Expression<Func<TEntity, decimal>> selector, QueryOptions<TEntity>? options = null) => SumAsync(selector, options).GetAwaiter().GetResult();
    /// <inheritdoc />
    public decimal? Sum(Expression<Func<TEntity, decimal?>> selector, QueryOptions<TEntity>? options = null) => SumAsync(selector, options).GetAwaiter().GetResult();

    // ---------------------------------------------------------------- dispose

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases resources. The repository does not own the unit of work, so this is a no-op by default.</summary>
    /// <param name="disposing">Whether the call comes from <see cref="Dispose()" />.</param>
    protected virtual void Dispose(bool disposing)
    {
    }

    // ---------------------------------------------------------------- query plumbing

    private async Task<List<TEntity>> SelectAsync(QueryOptions<TEntity>? options, int? limit, CancellationToken cancellationToken,
        Expression<Func<TEntity, bool>>? extraFilter = null, IReadOnlyCollection<string>? mapColumns = null)
    {
        var plan = GatedPlan(options, extraFilter);
        var residuals = Compile(plan.Residual);
        var selected = SelectedColumns(mapColumns, plan);

        // The engine span carries what no driver instrumentation can know: residual, split and gate facts.
        using var activity = DataActivitySource.Instance.StartActivity("cassandra.select", ActivityKind.Client);
        if (activity is not null)
        {
            activity.SetTag("db.system", "cassandra");
            activity.SetTag("equantic.client_evaluation", residuals.Count > 0);
            activity.SetTag("equantic.split_queries", plan.Alternatives.Count);
            activity.SetTag("equantic.partition_scoped", plan.PartitionScoped);
            activity.SetTag("equantic.allow_filtering", plan.RequiresAllowFiltering);
        }

        if (plan.Alternatives.Count > 0)
        {
            return await SelectSplitAsync(plan, residuals, selected, options, limit).ConfigureAwait(false);
        }

        // A CQL LIMIT would cut rows before the residual filter sees them, so it only pushes when nothing is residual.
        var pushLimit = limit.HasValue && residuals.Count == 0;
        var cql = SelectCql(selected, plan.Where, plan.RequiresAllowFiltering, options, pushLimit);
        var values = pushLimit ? [.. plan.Values, limit!.Value] : plan.Values;

        var rows = await CassandraStatements.ExecuteAsync(Session, cql, values,
            CassandraQueryOptionsExtensions.ConsistencyOf(options), UnitOfWork.CommandLogger, UnitOfWork.SensitiveLogging).ConfigureAwait(false);

        // The RowSet pages lazily; composing Where/Take before materializing the list stops fetching once satisfied.
        IEnumerable<TEntity> entities = rows.Select(row => CassandraMapper.Materialize<TEntity>(_configuration, row, selected));
        if (residuals.Count > 0)
        {
            entities = entities.Where(entity => residuals.All(residual => residual(entity)));
        }

        if (limit is { } take && !pushLimit)
        {
            entities = entities.Take(take);
        }

        return entities.ToList();
    }

    /// <summary>
    ///     Executes an OR-split plan: one native partition query per branch, in parallel, merged and
    ///     de-duplicated by primary key. A per-branch LIMIT keeps the global top-N correct (every merged top-N
    ///     row is inside some branch's top-N); the merge re-applies order and limit client-side.
    /// </summary>
    private async Task<List<TEntity>> SelectSplitAsync(CassandraCqlPlan plan, List<Func<TEntity, bool>> residuals,
        HashSet<string>? selected, QueryOptions<TEntity>? options, int? limit)
    {
        var pushLimit = limit.HasValue && residuals.Count == 0;
        var consistency = CassandraQueryOptionsExtensions.ConsistencyOf(options);
        var executions = plan.Alternatives.Select(alternative =>
        {
            var where = plan.Where.Length > 0 ? $"{plan.Where} AND {alternative.Where}" : alternative.Where;
            object?[] values = pushLimit
                ? [.. plan.Values, .. alternative.Values, limit!.Value]
                : [.. plan.Values, .. alternative.Values];
            return CassandraStatements.ExecuteAsync(Session,
                SelectCql(selected, where, plan.RequiresAllowFiltering, options, pushLimit), values, consistency);
        }).ToList();

        var rowSets = await Task.WhenAll(executions).ConfigureAwait(false);

        IEnumerable<TEntity> entities = rowSets.SelectMany(rows =>
            rows.Select(row => CassandraMapper.Materialize<TEntity>(_configuration, row, selected)));
        entities = DistinctByPrimaryKey(entities);
        if (residuals.Count > 0)
        {
            entities = entities.Where(entity => residuals.All(residual => residual(entity)));
        }

        if (options?.Sortings is { Count: > 0 } sortings)
        {
            entities = ClientSort(entities, sortings);
        }

        if (limit is { } take)
        {
            entities = entities.Take(take);
        }

        return entities.ToList();
    }

    private CassandraCqlPlan GatedPlan(QueryOptions<TEntity>? options, Expression<Func<TEntity, bool>>? extraFilter)
    {
        if (options is { IncludePaths.Count: > 0 })
        {
            throw new NotSupportedException(
                "Cassandra rows are self-contained; there are no navigations to include — model related data with the partition key or query it explicitly.");
        }

        var plan = CassandraCql.Plan(_configuration, options, extraFilter, GlobalFilter(options));

        if (plan.RequiresAllowFiltering && !CassandraCql.AllowFilteringOptedIn(options))
        {
            throw new NotSupportedException(
                "This filter targets non-key columns; call .AllowFiltering() to opt into a scan, or filter by the partition/clustering keys.");
        }

        if (plan.Residual.Count > 0)
        {
            if (!CassandraCql.ClientEvaluationOptedIn(options))
            {
                throw new NotSupportedException(
                    $"The clause(s) '{plan.ResidualText}' cannot be expressed in CQL; call .AllowClientEvaluation() to run them client-side " +
                    "over the pushed-down rows, or restructure the filter around the partition/clustering keys.");
            }

            if (!plan.PartitionScoped && !CassandraCql.AllowFilteringOptedIn(options))
            {
                throw new NotSupportedException(
                    "The client-evaluated filter is not scoped to a partition, so the fetch scans the whole table; " +
                    "call .AllowFiltering() as well to acknowledge the scan.");
            }
        }

        // The gates the opt-ins allowed through: logged and counted, so a hot path quietly leaning on them is
        // an alert in production, not an archaeology project.
        if (plan.RequiresAllowFiltering)
        {
            DataMetrics.AllowFiltering.Add(1, new KeyValuePair<string, object?>("db.system", "cassandra"));
            Microsoft.Extensions.Logging.LoggerExtensions.Log(UnitOfWork.CommandLogger,
                Microsoft.Extensions.Logging.LogLevel.Warning, DataEvents.AllowFiltering,
                "ALLOW FILTERING engaged for '{Entity}' (server-side scan)", typeof(TEntity).Name);
        }

        if (plan.Residual.Count > 0)
        {
            DataMetrics.ClientEvaluations.Add(1, new KeyValuePair<string, object?>("db.system", "cassandra"));
            Microsoft.Extensions.Logging.LoggerExtensions.Log(UnitOfWork.CommandLogger,
                Microsoft.Extensions.Logging.LogLevel.Warning, DataEvents.ClientEvaluation,
                "Client evaluation engaged for '{Entity}': {Residual}", typeof(TEntity).Name, plan.ResidualText);
        }

        if (plan.Alternatives.Count > 0)
        {
            DataMetrics.QuerySplits.Add(1, new KeyValuePair<string, object?>("db.system", "cassandra"));
            Microsoft.Extensions.Logging.LoggerExtensions.Log(UnitOfWork.CommandLogger,
                Microsoft.Extensions.Logging.LogLevel.Warning, DataEvents.QuerySplit,
                "OR split into {Branches} native queries for '{Entity}'", plan.Alternatives.Count, typeof(TEntity).Name);
        }

        return plan;
    }

    private string SelectCql(IReadOnlySet<string>? selected, string where, bool allowFiltering, QueryOptions<TEntity>? options, bool pushLimit)
    {
        var columns = selected is null
            ? "*"
            : string.Join(", ", _configuration.Columns.Where(column => selected.Contains(column.Name)).Select(column => column.Name));

        var cql = $"SELECT {columns} FROM {_configuration.TableName}";
        if (where.Length > 0)
        {
            cql += $" WHERE {where}";
        }

        cql += OrderBy(options);
        if (pushLimit)
        {
            cql += " LIMIT ?";
        }

        if (allowFiltering)
        {
            cql += " ALLOW FILTERING";
        }

        return cql;
    }

    /// <summary>The columns the projected SELECT must fetch: the map's columns plus the residual's; null → all.</summary>
    private HashSet<string>? SelectedColumns(IReadOnlyCollection<string>? mapColumns, CassandraCqlPlan plan)
    {
        if (mapColumns is null)
        {
            return null;
        }

        // The collectors surface CLR member names; the selected set lives in stored-column space.
        var selected = new HashSet<string>(mapColumns.Select(_configuration.ColumnFor), StringComparer.OrdinalIgnoreCase);
        if (plan.Alternatives.Count > 0)
        {
            // The split merge de-duplicates by primary key, so a projected read must fetch the key columns too.
            selected.UnionWith(CassandraMapper.PrimaryKey(_configuration));
        }

        if (plan.Residual.Count > 0)
        {
            var residualColumns = ColumnsOf(plan.Residual);
            if (residualColumns is null)
            {
                return null;
            }

            selected.UnionWith(residualColumns.Select(_configuration.ColumnFor));
        }

        if (selected.Count == 0)
        {
            selected.Add(_configuration.KeyColumn);
        }

        return selected;
    }

    /// <summary>Filters out duplicate rows across OR-split branches by their primary key values.</summary>
    private IEnumerable<TEntity> DistinctByPrimaryKey(IEnumerable<TEntity> entities)
    {
        var properties = CassandraMapper.PrimaryKey(_configuration)
            .Select(column => typeof(TEntity).GetProperty(_configuration.MemberFor(column),
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase))
            .Where(property => property is not null)
            .ToArray();

        var seen = new HashSet<string>();
        foreach (var entity in entities)
        {
            if (seen.Add(string.Join("\u0001", properties.Select(property => property!.GetValue(entity)))))
            {
                yield return entity;
            }
        }
    }

    /// <summary>Re-applies the requested ordering over a merged result set (each branch arrives ordered on its own).</summary>
    private static IEnumerable<TEntity> ClientSort(IEnumerable<TEntity> entities, IReadOnlyList<QuerySort<TEntity>> sortings)
    {
        IOrderedEnumerable<TEntity>? ordered = null;
        foreach (var sort in sortings)
        {
            var key = Boxed(sort.KeySelector);
            ordered = ordered is null
                ? sort.Direction == SortDirection.Descending ? entities.OrderByDescending(key) : entities.OrderBy(key)
                : sort.Direction == SortDirection.Descending ? ordered.ThenByDescending(key) : ordered.ThenBy(key);
        }

        return ordered ?? entities;
    }

    private static Func<TEntity, object?> Boxed(LambdaExpression keySelector) =>
        Expression.Lambda<Func<TEntity, object?>>(
            Expression.Convert(keySelector.Body, typeof(object)), keySelector.Parameters).Compile();

    private async Task<TSum> SumCoreAsync<TSum>(LambdaExpression selector, QueryOptions<TEntity>? options,
        CancellationToken cancellationToken, Func<List<TEntity>, TSum> clientSum)
    {
        var column = SumColumn(selector);
        if (column is not null)
        {
            var plan = GatedPlan(options, null);
            if (plan.Residual.Count == 0 && plan.Alternatives.Count == 0)
            {
                var cql = $"SELECT SUM({column}) FROM {_configuration.TableName}"
                          + (plan.Where.Length > 0 ? $" WHERE {plan.Where}" : string.Empty)
                          + (plan.RequiresAllowFiltering ? " ALLOW FILTERING" : string.Empty);
                var row = (await CassandraStatements.ExecuteAsync(Session, cql, plan.Values,
                    CassandraQueryOptionsExtensions.ConsistencyOf(options), UnitOfWork.CommandLogger, UnitOfWork.SensitiveLogging).ConfigureAwait(false)).First();
                return row.IsNull(0) ? default! : row.GetValue<TSum>(0);
            }
        }

        // Computed selector or residual filter: materialize and aggregate client-side (same behaviour as before).
        return clientSum(await SelectAsync(options, null, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The single column a <c>SUM</c> can push down, or null for a computed selector.</summary>
    private string? SumColumn(LambdaExpression selector)
    {
        var body = selector.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            body = unary.Operand;
        }

        return body is MemberExpression { Expression: ParameterExpression } member
            ? _configuration.Columns.FirstOrDefault(column =>
                CassandraEntityConfiguration.Same(column.Member, member.Member.Name))?.Name
            : null;
    }

    private string OrderBy(QueryOptions<TEntity>? options)
    {
        if (options?.Sortings is not { Count: > 0 } sortings)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var sort in sortings)
        {
            var member = sort.KeySelector.GetMemberName();
            var column = _configuration.ColumnFor(member);
            if (!_configuration.IsClusteringKey(column))
            {
                throw new NotSupportedException($"Cassandra can only ORDER BY clustering keys; '{member}' is not one.");
            }

            parts.Add($"{column} {(sort.Direction == SortDirection.Descending ? "DESC" : "ASC")}");
        }

        return " ORDER BY " + string.Join(", ", parts);
    }

    /// <summary>The global filter for this entity, unless the options opt out of query filters.</summary>
    private Expression<Func<TEntity, bool>>? GlobalFilter(QueryOptions<TEntity>? options) =>
        options is { IgnoreQueryFilters: true } ? null : UnitOfWork.GlobalFilter<TEntity>();

    /// <summary>Builds <c>x =&gt; x.Key == id</c> over the configured key column, routed through the CQL translator.</summary>
    private Expression<Func<TEntity, bool>> IdPredicate(TKey id)
    {
        var body = Expression.Equal(_keySelector.Body, Expression.Constant(id, _keySelector.Body.Type));
        return Expression.Lambda<Func<TEntity, bool>>(body, _keySelector.Parameters);
    }

    private static List<Func<TEntity, bool>> Compile(IReadOnlyList<LambdaExpression> residual) =>
        residual.Select(predicate => ((Expression<Func<TEntity, bool>>)predicate).Compile()).ToList();

    /// <summary>The first-segment columns the map reads from the entity, or null when it needs the whole entity.</summary>
    private static IReadOnlyCollection<string>? MapColumns(LambdaExpression map)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return ColumnCollector.TryCollect(map, columns) ? columns : null;
    }

    /// <summary>The union of the columns the residual predicates read, or null when one needs the whole entity.</summary>
    private static HashSet<string>? ColumnsOf(IReadOnlyList<LambdaExpression> predicates)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var predicate in predicates)
        {
            if (!ColumnCollector.TryCollect(predicate, columns))
            {
                return null;
            }
        }

        return columns;
    }

    private static T NotNull<T>(T value) where T : class => value ?? throw new ArgumentNullException(typeof(T).Name);

    /// <summary>
    ///     Collects the first-segment members a lambda reads from its parameter (the columns a projected
    ///     <c>SELECT</c> must fetch); reports failure when the lambda uses the parameter itself, meaning the whole
    ///     entity is needed.
    /// </summary>
    private sealed class ColumnCollector : ExpressionVisitor
    {
        private readonly ParameterExpression _parameter;
        private readonly HashSet<string> _columns;
        private bool _wholeEntity;

        private ColumnCollector(ParameterExpression parameter, HashSet<string> columns)
        {
            _parameter = parameter;
            _columns = columns;
        }

        public static bool TryCollect(LambdaExpression lambda, HashSet<string> columns)
        {
            var collector = new ColumnCollector(lambda.Parameters[0], columns);
            collector.Visit(lambda.Body);
            return !collector._wholeEntity;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            Expression? root = node;
            MemberExpression? closest = null;
            while (root is MemberExpression member)
            {
                closest = member;
                root = member.Expression;
            }

            if (ReferenceEquals(root, _parameter))
            {
                // The chain roots at the entity: its first segment is the fetched column; don't descend
                // into the chain, or the parameter underneath would read as a whole-entity use.
                _columns.Add(closest!.Member.Name);
                return node;
            }

            return base.VisitMember(node);
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (ReferenceEquals(node, _parameter))
            {
                _wholeEntity = true;
            }

            return node;
        }
    }
}
