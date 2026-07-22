using System.Data.Common;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using eQuantic.Core.Data.Diagnostics;
using eQuantic.Core.Data.Query;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Read;
using eQuantic.Linq.Expressions;
using eQuantic.Linq.Specification;
using eQuantic.Linq.Web;

namespace eQuantic.Core.Data.Relational.Repository;

/// <summary>
///     The native relational read repository. A <see cref="QueryOptions{TEntity}" /> filter renders to a
///     parameterized SQL <c>WHERE</c> — SQL is a complete target for the filter model, so <c>OR</c>/<c>NOT</c>/
///     <c>!=</c>/<c>NULL</c> push down whole and only arbitrary predicates degrade to the gated client-side
///     residual (<c>.AllowClientEvaluation()</c>). Sorting works on any mapped column, paging is native
///     <c>LIMIT/OFFSET</c> plus keyset continuation, aggregates and projections push down, and reads join the
///     open transaction. Synchronous members delegate to the asynchronous ones.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TKey">The key type.</typeparam>
public abstract class RelationalReadRepository<TEntity, TKey> :
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
    protected readonly RelationalUnitOfWork UnitOfWork;

    private readonly RelationalEntityConfiguration _configuration;
    private readonly SqlDialect _dialect;

    /// <summary>Initializes the repository over a unit of work.</summary>
    /// <param name="unitOfWork">The queryable unit of work (a <see cref="RelationalUnitOfWork" />).</param>
    protected RelationalReadRepository(IQueryableUnitOfWork unitOfWork)
    {
        UnitOfWork = unitOfWork as RelationalUnitOfWork
                     ?? throw new ArgumentException($"The unit of work must be a {nameof(RelationalUnitOfWork)}.", nameof(unitOfWork));
        _configuration = UnitOfWork.Configuration<TEntity>();
        _dialect = UnitOfWork.Dialect;
    }

    // ---------------------------------------------------------------- asynchronous reads

    /// <inheritdoc />
    public async Task<TEntity?> GetAsync(TKey id, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        (await SelectAsync(options, 1, null, false, cancellationToken, IdPredicate(id)).ConfigureAwait(false)).FirstOrDefault();

    /// <inheritdoc />
    public async Task<IEnumerable<TEntity>> GetAllAsync(QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await SelectAsync(options, null, null, false, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IEnumerable<TEntity>> GetFilteredAsync(Expression<Func<TEntity, bool>> filter, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await SelectAsync(options, null, null, false, cancellationToken, NotNull(filter)).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IEnumerable<TEntity>> AllMatchingAsync(ISpecification<TEntity> specification, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await SelectAsync(options, null, null, false, cancellationToken, NotNull(specification).SatisfiedBy()).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IEnumerable<TResult>> GetMappedAsync<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        (await SelectAsync(options, null, null, false, cancellationToken, null, MapColumns(NotNull(map))).ConfigureAwait(false)).Select(map.Compile()).ToList();

    /// <inheritdoc />
    public async Task<TEntity?> GetFirstAsync(QueryOptions<TEntity> options, CancellationToken cancellationToken = default) =>
        (await SelectAsync(options, 1, null, false, cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    /// <inheritdoc />
    public async Task<TResult?> GetFirstMappedAsync<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity> options, CancellationToken cancellationToken = default) =>
        (await SelectAsync(options, 1, null, false, cancellationToken, null, MapColumns(NotNull(map))).ConfigureAwait(false)).Select(map.Compile()).FirstOrDefault();

    /// <inheritdoc />
    public async Task<TEntity?> GetSingleAsync(QueryOptions<TEntity> options, CancellationToken cancellationToken = default) =>
        (await SelectAsync(options, 2, null, false, cancellationToken).ConfigureAwait(false)).SingleOrDefault();

    /// <inheritdoc />
    public async Task<PagedResult<TEntity>> GetPagedAsync(PageRequest page, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        var total = await CountAsync(options, cancellationToken).ConfigureAwait(false);
        var items = await SelectAsync(options, page.Take, page.Skip, true, cancellationToken).ConfigureAwait(false);
        return new PagedResult<TEntity>(items, total, page.PageIndex, page.PageSize);
    }

    /// <inheritdoc />
    public async Task<PagedResult<TResult>> GetPagedAsync<TResult>(PageRequest page, Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        var total = await CountAsync(options, cancellationToken).ConfigureAwait(false);
        var rows = await SelectAsync(options, page.Take, page.Skip, true, cancellationToken, null, MapColumns(NotNull(map))).ConfigureAwait(false);
        return new PagedResult<TResult>(rows.Select(map.Compile()).ToList(), total, page.PageIndex, page.PageSize);
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        var plan = GatedPlan(options, null);
        if (plan.Residual.Count == 0)
        {
            var sql = $"SELECT COUNT(*) FROM {_dialect.Quote(_configuration.TableName)}"
                      + (plan.Where.Length > 0 ? $" WHERE {plan.Where}" : string.Empty);
            return await UnitOfWork.RetryAsync(write: false, async _ =>
            {
                await using var command = await UnitOfWork.CommandAsync(sql, plan.Parameters, cancellationToken).ConfigureAwait(false);
                var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return Convert.ToInt64(result);
            }, cancellationToken).ConfigureAwait(false);
        }

        return (await SelectAsync(options, null, null, false, cancellationToken).ConfigureAwait(false)).Count;
    }

    /// <inheritdoc />
    public async Task<bool> AnyAsync(QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        (await SelectAsync(options, 1, null, false, cancellationToken).ConfigureAwait(false)).Count > 0;

    /// <inheritdoc />
    public async Task<bool> AllAsync(Expression<Func<TEntity, bool>> predicate, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        (await SelectAsync(options, null, null, false, cancellationToken).ConfigureAwait(false)).All(predicate.Compile());

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

    // ---------------------------------------------------------------- raw SQL (escape hatch)

    /// <summary>
    ///     The escape hatch: runs arbitrary SQL (joins, CTEs, window functions — whatever the engine cannot
    ///     express yet) and materializes rows into entities by <b>column name</b> against the mapped columns;
    ///     unmatched result columns are ignored. Parameters bind as <c>@p0…@pN</c> in order. The command joins
    ///     the open transaction. Global query filters do <b>not</b> apply — the SQL is yours.
    /// </summary>
    /// <param name="sql">The SQL text.</param>
    /// <param name="parameters">The values bound as <c>@p0…</c>, in order.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The materialized entities.</returns>
    public Task<IReadOnlyList<TEntity>> FromSqlAsync(string sql, IReadOnlyList<object?>? parameters = null,
        CancellationToken cancellationToken = default) =>
        UnitOfWork.RetryAsync<IReadOnlyList<TEntity>>(write: false, async _ =>
        {
            await using var command = await UnitOfWork.CommandAsync(sql,
                (parameters ?? []).Select(_dialect.BindValue).ToList(), cancellationToken).ConfigureAwait(false);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var entities = new List<TEntity>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                entities.Add(RelationalMaterializer.MaterializeByName<TEntity>(reader, _configuration));
            }

            return entities;
        }, cancellationToken);

    // ---------------------------------------------------------------- min / max / average

    /// <inheritdoc />
    public async Task<TResult?> MinAsync<TResult>(Expression<Func<TEntity, TResult>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await AggregateAsync(selector, options, cancellationToken,
            column => $"MIN({_dialect.Quote(column.Name)})",
            value => (TResult?)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(TResult)) ?? typeof(TResult)),
            rows => rows.Count == 0 ? default : rows.Min(selector.Compile())).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<TResult?> MaxAsync<TResult>(Expression<Func<TEntity, TResult>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await AggregateAsync(selector, options, cancellationToken,
            column => $"MAX({_dialect.Quote(column.Name)})",
            value => (TResult?)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(TResult)) ?? typeof(TResult)),
            rows => rows.Count == 0 ? default : rows.Max(selector.Compile())).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<double> AverageAsync<TValue>(Expression<Func<TEntity, TValue>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default) =>
        await AggregateAsync(selector, options, cancellationToken,
            column => $"AVG(CAST({_dialect.Quote(column.Name)} AS {_dialect.SqlType(typeof(double))}))",
            Convert.ToDouble,
            rows => rows.Count == 0 ? 0d : rows.Average(row => Convert.ToDouble(selector.Compile()(row)))).ConfigureAwait(false);

    private async Task<TResult> AggregateAsync<TSelected, TResult>(Expression<Func<TEntity, TSelected>> selector,
        QueryOptions<TEntity>? options, CancellationToken cancellationToken,
        Func<RelationalColumn, string> aggregate, Func<object, TResult> convert, Func<List<TEntity>, TResult> clientFallback)
    {
        var column = SumColumn(selector);
        if (column is not null)
        {
            var plan = GatedPlan(options, null);
            if (plan.Residual.Count == 0)
            {
                var sql = $"SELECT {aggregate(column)} FROM {_dialect.Quote(_configuration.TableName)}"
                          + (plan.Where.Length > 0 ? $" WHERE {plan.Where}" : string.Empty);
                return await UnitOfWork.RetryAsync(write: false, async _ =>
                {
                    await using var command = await UnitOfWork.CommandAsync(sql, plan.Parameters, cancellationToken).ConfigureAwait(false);
                    var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    return result is null or DBNull ? default! : convert(result);
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        return clientFallback(await SelectAsync(options, null, null, false, cancellationToken).ConfigureAwait(false));
    }

    // ---------------------------------------------------------------- grouped reads

    /// <inheritdoc />
    /// <remarks>
    ///     Renders to a native <c>GROUP BY</c> — the filter pushes into the <c>WHERE</c> (before grouping), the
    ///     <paramref name="having" /> predicate into <c>HAVING</c> (over the groups), and only the grouped rows
    ///     travel. A filter with a client-side residual degrades, behind <c>.AllowClientEvaluation()</c>, to
    ///     fetching the matching rows and grouping (and having-filtering) them with the selectors themselves —
    ///     identical semantics, explicit cost. Group order is store-defined; order the result client-side.
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

        if (options is { IncludePaths.Count: > 0 })
        {
            throw new NotSupportedException("Include does not apply to a grouped projection; drop the includes.");
        }

        // Interpret first so the contract is uniform: only server-side aggregate shapes are accepted on every path.
        var group = GroupInterpreter.Interpret(NotNull(keySelector), NotNull(resultSelector));
        var predicate = having is null ? null : GroupInterpreter.InterpretHaving(having, group.Key);
        var plan = GatedPlan(options, null);

        if (plan.Residual.Count > 0)
        {
            var rows = await SelectAsync(options, null, null, false, cancellationToken).ConfigureAwait(false);
            var groups = rows.GroupBy(keySelector.Compile());
            if (having is not null)
            {
                groups = groups.Where(having.Compile());
            }

            return groups.Select(resultSelector.Compile()).ToList();
        }

        var keys = group.Key
            .Select(member => _configuration.ColumnFor(member.Path)
                              ?? throw new NotSupportedException(
                                  $"'{typeof(TEntity).Name}' has no mapped member '{member.Path}' to group by."))
            .ToList();

        var select = keys.Select(column => _dialect.Quote(column.Name))
            .Concat(group.Bindings.OfType<GroupAggregateBinding>()
                .Select(aggregate => AggregateSql(aggregate.Aggregate, aggregate.Member)))
            .ToList();

        var grouped = string.Join(", ", keys.Select(column => _dialect.Quote(column.Name)));
        var sql = $"SELECT {string.Join(", ", select)} FROM {_dialect.Quote(_configuration.TableName)}"
                  + (plan.Where.Length > 0 ? $" WHERE {plan.Where}" : string.Empty)
                  + $" GROUP BY {grouped}"
                  + (predicate is not null ? $" HAVING {HavingSql(predicate)}" : string.Empty);

        await using var command = await UnitOfWork.CommandAsync(sql, plan.Parameters, cancellationToken).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var project = GroupProjector<TGroup, TResult>(group);
        var results = new List<TResult>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(project(reader));
        }

        return results;

        string AggregateSql(GroupAggregate aggregate, string? member)
        {
            if (aggregate == GroupAggregate.Count)
            {
                return "COUNT(*)";
            }

            var column = _configuration.ColumnFor(member!)
                         ?? throw new NotSupportedException(
                             $"'{typeof(TEntity).Name}' has no mapped member '{member}' to aggregate.");
            var quoted = _dialect.Quote(column.Name);
            return aggregate switch
            {
                GroupAggregate.Sum => $"SUM({quoted})",
                GroupAggregate.Min => $"MIN({quoted})",
                GroupAggregate.Max => $"MAX({quoted})",
                _ => $"AVG(CAST({quoted} AS {_dialect.SqlType(typeof(double))}))",
            };
        }

        string HavingSql(GroupPredicate node) => node switch
        {
            GroupLogical logical => "(" + string.Join(
                logical.Operator == LogicalOperator.And ? " AND " : " OR ",
                logical.Operands.Select(HavingSql)) + ")",
            GroupComparison comparison => ComparisonSql(comparison),
            _ => throw new NotSupportedException($"Unknown group predicate '{node.GetType().Name}'."),
        };

        string ComparisonSql(GroupComparison comparison)
        {
            var fragment = comparison.Aggregate is { } aggregate
                ? AggregateSql(aggregate, comparison.Member)
                : _dialect.Quote((_configuration.ColumnFor(comparison.Member!)
                                  ?? throw new NotSupportedException(
                                      $"'{typeof(TEntity).Name}' has no mapped member '{comparison.Member}' in HAVING.")).Name);

            if (comparison.Value is null)
            {
                return comparison.Operator switch
                {
                    ComparisonOperator.Equal => $"{fragment} IS NULL",
                    ComparisonOperator.NotEqual => $"{fragment} IS NOT NULL",
                    _ => throw new NotSupportedException("Only == null and != null compare a group value against null."),
                };
            }

            plan.Parameters.Add(_dialect.BindValue(comparison.Value));
            return $"{fragment} {ComparisonToken(comparison.Operator)} @p{plan.Parameters.Count - 1}";
        }

        static string ComparisonToken(ComparisonOperator op) => op switch
        {
            ComparisonOperator.Equal => "=",
            ComparisonOperator.NotEqual => "<>",
            ComparisonOperator.GreaterThan => ">",
            ComparisonOperator.GreaterThanOrEqual => ">=",
            ComparisonOperator.LessThan => "<",
            _ => "<=",
        };
    }

    /// <summary>
    ///     Builds the row projector for a grouped SELECT laid out as key columns first, aggregates after, in
    ///     binding order — reading each slot into the result member's type.
    /// </summary>
    private static Func<DbDataReader, TResult> GroupProjector<TGroup, TResult>(GroupQuery group)
    {
        var projector = new RelationalProjector<TResult>(
            group.Bindings.Select(binding => binding.Target).ToList(), group.ConstructorProjection);
        var keyConstructor = group.Key is [{ Name: not null }, ..] ? typeof(TGroup).GetConstructors().Single() : null;
        var aggregateOrdinals = new Dictionary<GroupBinding, int>();
        foreach (var binding in group.Bindings.OfType<GroupAggregateBinding>())
        {
            aggregateOrdinals.Add(binding, group.Key.Count + aggregateOrdinals.Count);
        }

        return reader =>
        {
            var keyValues = new object?[group.Key.Count];
            for (var ordinal = 0; ordinal < keyValues.Length; ordinal++)
            {
                keyValues[ordinal] = reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
            }

            var values = new object?[group.Bindings.Count];
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = group.Bindings[index] switch
                {
                    GroupKeyBinding { KeyName: null } when keyConstructor is null =>
                        RelationalMaterializer.ChangeValue(keyValues[0], typeof(TGroup)),
                    GroupKeyBinding { KeyName: null } => keyConstructor.Invoke(keyConstructor.GetParameters()
                        .Select((parameter, position) => RelationalMaterializer.ChangeValue(keyValues[position], parameter.ParameterType))
                        .ToArray()),
                    GroupKeyBinding named => RelationalMaterializer.ChangeValue(
                        keyValues[KeyIndex(group.Key, named.KeyName!)], projector.TargetType(index)),
                    var aggregate => RelationalMaterializer.ChangeValue(
                        reader.IsDBNull(aggregateOrdinals[aggregate]) ? null : reader.GetValue(aggregateOrdinals[aggregate]),
                        projector.TargetType(index)),
                };
            }

            return projector.Create(values);
        };

        static int KeyIndex(IReadOnlyList<GroupKeyMember> key, string name)
        {
            for (var index = 0; index < key.Count; index++)
            {
                if (string.Equals(key[index].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            throw new NotSupportedException($"The key has no member '{name}'.");
        }
    }

    // ---------------------------------------------------------------- continuation paging (keyset)

    /// <inheritdoc />
    /// <remarks>Pages by keyset over the key column: <c>key &gt; last ORDER BY key LIMIT n</c> — deep pages cost an index seek.</remarks>
    public async Task<ContinuedResult<TEntity>> GetPageAsync(int pageSize, string? continuationToken = null,
        QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default)
    {
        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "The page size must be at least 1.");
        }

        if (options is { Sortings.Count: > 0 })
        {
            throw new NotSupportedException(
                "Keyset paging orders by the key; custom sortings are not supported — use GetPagedAsync, or sort each page client-side.");
        }

        var extra = continuationToken is null ? null : KeyAfter(ReadToken(continuationToken));
        var items = await SelectAsync(options, pageSize, null, true, cancellationToken, extra).ConfigureAwait(false);
        var token = items.Count < pageSize ? null : WriteToken(_configuration.Key.Property.GetValue(items[^1]));
        return new ContinuedResult<TEntity>(items, token);
    }

    // ---------------------------------------------------------------- streaming

    /// <inheritdoc />
    public async IAsyncEnumerable<TEntity> GetStreamAsync(QueryOptions<TEntity>? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (options is { IncludePaths.Count: > 0 })
        {
            throw new NotSupportedException("Include does not compose with streaming yet; use GetAllAsync or page explicitly.");
        }

        var plan = GatedPlan(options, null);
        var residuals = Compile(plan.Residual);
        var selected = _configuration.Columns;
        var sql = SelectSql(selected, plan.Where, options, false, null, null);

        await using var command = await UnitOfWork.CommandAsync(sql, plan.Parameters, cancellationToken).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var entity = RelationalMaterializer.Materialize<TEntity>(reader, selected);
            if (residuals.Count == 0 || residuals.All(residual => residual(entity)))
            {
                yield return entity;
            }
        }
    }

    // ---------------------------------------------------------------- explain

    /// <inheritdoc />
    public QueryPlan Explain(QueryOptions<TEntity>? options = null)
    {
        var notes = new List<string>();
        var global = GlobalFilter(options);
        if (global is not null)
        {
            notes.Add("A global query filter is ANDed into this query; IgnoringQueryFilters() opts out.");
        }

        if (options is { IncludePaths.Count: > 0 })
        {
            notes.Add($"Each include ({string.Join(", ", options.IncludePaths)}) runs as one follow-up IN query.");
        }

        var plan = RelationalSql.Plan(_dialect, _configuration, options, null, global);
        var sql = SelectSql(_configuration.Columns, plan.Where, options, false, null, null);

        if (plan.Residual.Count > 0)
        {
            if (!RelationalQueryOptionsExtensions.IsClientEvaluationOptedIn(options))
            {
                notes.Add("Requires .AllowClientEvaluation(): part of the filter cannot be expressed in SQL and runs client-side.");
            }

            notes.Add("Rows are fetched without LIMIT/OFFSET and filtered client-side; paging applies after the residual.");
        }

        notes.Add("Partition scoping does not apply to a relational store.");

        return new QueryPlan(
            _dialect.System,
            sql,
            plan.Parameters,
            plan.Residual.Count > 0 ? plan.ResidualText : null,
            serverSideFiltering: false,
            clientEvaluation: plan.Residual.Count > 0,
            partitionScoped: false,
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

    internal Task<List<TEntity>> SelectAsync(QueryOptions<TEntity>? options, int? limit, int? offset,
        bool orderByKeyWhenUnsorted, CancellationToken cancellationToken,
        Expression<Func<TEntity, bool>>? extraFilter = null, IReadOnlyCollection<string>? mapColumns = null) =>
        // The read funnel runs whole under the transient-retry policy (a no-op unless one is registered).
        UnitOfWork.RetryAsync(write: false,
            _ => SelectCoreAsync(options, limit, offset, orderByKeyWhenUnsorted, cancellationToken, extraFilter, mapColumns),
            cancellationToken);

    private async Task<List<TEntity>> SelectCoreAsync(QueryOptions<TEntity>? options, int? limit, int? offset,
        bool orderByKeyWhenUnsorted, CancellationToken cancellationToken,
        Expression<Func<TEntity, bool>>? extraFilter = null, IReadOnlyCollection<string>? mapColumns = null)
    {
        var plan = GatedPlan(options, extraFilter);
        var residuals = Compile(plan.Residual);
        var selected = SelectedColumns(mapColumns, plan);

        using var activity = DataActivitySource.Instance.StartActivity($"{_dialect.System}.select", ActivityKind.Client);
        if (activity is not null)
        {
            activity.SetTag("db.system", _dialect.System);
            activity.SetTag("equantic.client_evaluation", residuals.Count > 0);
        }

        // LIMIT/OFFSET would cut/shift rows before the residual filter sees them, so they only push when nothing is residual.
        var push = residuals.Count == 0;
        var sql = SelectSql(selected, plan.Where, options, orderByKeyWhenUnsorted || offset is not null,
            push ? limit : null, push ? offset : null, plan.Parameters);

        await using var command = await UnitOfWork.CommandAsync(sql, plan.Parameters, cancellationToken).ConfigureAwait(false);
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        // The reader must close before the include loader issues follow-up commands on the same connection.
        var entities = new List<TEntity>();
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var entity = RelationalMaterializer.Materialize<TEntity>(reader, selected);
                if (residuals.Count == 0 || residuals.All(residual => residual(entity)))
                {
                    entities.Add(entity);
                    if (push && limit is { } take && entities.Count >= take)
                    {
                        break;
                    }
                }
            }
        }

        if (!push)
        {
            IEnumerable<TEntity> sliced = entities;
            if (offset is { } skip)
            {
                sliced = sliced.Skip(skip);
            }

            if (limit is { } take)
            {
                sliced = sliced.Take(take);
            }

            entities = sliced.ToList();
        }

        if (options is { IncludePaths.Count: > 0 })
        {
            if (mapColumns is not null)
            {
                throw new NotSupportedException(
                    "Include applies to entity reads; project after including, or drop the includes from the mapped read.");
            }

            await RelationalInclude.ApplyAsync(UnitOfWork, entities, options.IncludePaths, cancellationToken).ConfigureAwait(false);
        }

        return entities;
    }

    private RelationalSqlPlan GatedPlan(QueryOptions<TEntity>? options, Expression<Func<TEntity, bool>>? extraFilter)
    {
        var plan = RelationalSql.Plan(_dialect, _configuration, options, extraFilter, GlobalFilter(options));

        if (plan.Residual.Count > 0 && !RelationalQueryOptionsExtensions.IsClientEvaluationOptedIn(options))
        {
            throw new NotSupportedException(
                $"The clause(s) '{plan.ResidualText}' cannot be expressed in SQL; call .AllowClientEvaluation() to run them " +
                "client-side over the pushed-down rows, or restructure the filter.");
        }

        return plan;
    }

    private string SelectSql(IReadOnlyList<RelationalColumn> selected, string where, QueryOptions<TEntity>? options,
        bool orderByKeyWhenUnsorted, int? limit, int? offset, List<object?>? parameters = null)
    {
        var sql = $"SELECT {string.Join(", ", selected.Select(column => _dialect.Quote(column.Name)))}"
                  + $" FROM {_dialect.Quote(_configuration.TableName)}";
        if (where.Length > 0)
        {
            sql += $" WHERE {where}";
        }

        // A dialect whose limit syntax demands an ORDER BY (OFFSET/FETCH) gets a deterministic key order.
        sql += OrderBy(options, orderByKeyWhenUnsorted || (limit is not null && _dialect.RequiresOrderByForLimit));

        if (limit is not null && parameters is not null)
        {
            parameters.Add(limit);
            var limitParameter = "@p" + (parameters.Count - 1);
            string? offsetParameter = null;
            if (offset is not null)
            {
                parameters.Add(offset);
                offsetParameter = "@p" + (parameters.Count - 1);
            }

            sql += " " + _dialect.LimitClause(limitParameter, offsetParameter);
        }

        return sql;
    }

    private string OrderBy(QueryOptions<TEntity>? options, bool orderByKeyWhenUnsorted)
    {
        if (options?.Sortings is { Count: > 0 } sortings)
        {
            var parts = sortings.Select(sort =>
            {
                var column = _configuration.ColumnFor(sort.KeySelector.GetMemberName())
                             ?? throw new NotSupportedException(
                                 $"'{typeof(TEntity).Name}' has no mapped member '{sort.KeySelector.GetMemberName()}' to order by.");
                return $"{_dialect.Quote(column.Name)} {(sort.Direction == SortDirection.Descending ? "DESC" : "ASC")}";
            });
            return " ORDER BY " + string.Join(", ", parts);
        }

        return orderByKeyWhenUnsorted ? $" ORDER BY {_dialect.Quote(_configuration.Key.Name)}" : string.Empty;
    }

    private async Task<TSum> SumCoreAsync<TSum>(LambdaExpression selector, QueryOptions<TEntity>? options,
        CancellationToken cancellationToken, Func<List<TEntity>, TSum> clientSum)
    {
        var column = SumColumn(selector);
        if (column is not null)
        {
            var plan = GatedPlan(options, null);
            if (plan.Residual.Count == 0)
            {
                var sql = $"SELECT SUM({_dialect.Quote(column.Name)}) FROM {_dialect.Quote(_configuration.TableName)}"
                          + (plan.Where.Length > 0 ? $" WHERE {plan.Where}" : string.Empty);
                return await UnitOfWork.RetryAsync(write: false, async _ =>
                {
                    await using var command = await UnitOfWork.CommandAsync(sql, plan.Parameters, cancellationToken).ConfigureAwait(false);
                    var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    if (result is null or DBNull)
                    {
                        return default!;
                    }

                    var target = Nullable.GetUnderlyingType(typeof(TSum)) ?? typeof(TSum);
                    return (TSum)Convert.ChangeType(result, target);
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        return clientSum(await SelectAsync(options, null, null, false, cancellationToken).ConfigureAwait(false));
    }

    private RelationalColumn? SumColumn(LambdaExpression selector)
    {
        var body = selector.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            body = unary.Operand;
        }

        return body is MemberExpression { Expression: ParameterExpression } member
            ? _configuration.ColumnFor(member.Member.Name)
            : null;
    }

    /// <summary>The columns a projected SELECT must fetch: the map's columns plus the residual's; everything when unresolvable.</summary>
    private IReadOnlyList<RelationalColumn> SelectedColumns(IReadOnlyCollection<string>? mapColumns, RelationalSqlPlan plan)
    {
        if (mapColumns is null)
        {
            return _configuration.Columns;
        }

        var names = new HashSet<string>(mapColumns, StringComparer.OrdinalIgnoreCase);
        if (plan.Residual.Count > 0)
        {
            var residualColumns = ColumnsOf(plan.Residual);
            if (residualColumns is null)
            {
                return _configuration.Columns;
            }

            names.UnionWith(residualColumns);
        }

        var selected = _configuration.Columns.Where(column => names.Contains(column.Property.Name)).ToList();
        return selected.Count > 0 ? selected : [_configuration.Key];
    }

    private Expression<Func<TEntity, bool>>? GlobalFilter(QueryOptions<TEntity>? options) =>
        options is { IgnoreQueryFilters: true } ? null : UnitOfWork.GlobalFilter<TEntity>();

    private Expression<Func<TEntity, bool>> IdPredicate(TKey id) => KeyAfterOrEqual(id, ExpressionType.Equal);

    private Expression<Func<TEntity, bool>> KeyAfter(object? key) => KeyAfterOrEqual(key, ExpressionType.GreaterThan);

    private Expression<Func<TEntity, bool>> KeyAfterOrEqual(object? key, ExpressionType comparison)
    {
        var property = _configuration.Key.Property;
        var parameter = Expression.Parameter(typeof(TEntity), "e");
        var member = Expression.MakeMemberAccess(parameter, property);
        var value = Expression.Constant(key, property.PropertyType);

        Expression body;
        try
        {
            body = Expression.MakeBinary(comparison, member, value);
        }
        catch (InvalidOperationException)
        {
            // string/Guid have no comparison operators; CompareTo folds to the same ordering server-side.
            var compare = Expression.Call(member, property.PropertyType.GetMethod(nameof(IComparable.CompareTo), [property.PropertyType])!, value);
            body = Expression.MakeBinary(comparison, compare, Expression.Constant(0));
        }

        return Expression.Lambda<Func<TEntity, bool>>(body, parameter);
    }

    private string WriteToken(object? key) => key switch
    {
        null => throw new InvalidOperationException($"'{typeof(TEntity).Name}' produced a null key; keyset paging needs one."),
        _ => System.Text.Json.JsonSerializer.Serialize(key, _configuration.Key.Property.PropertyType),
    };

    private object? ReadToken(string token) =>
        System.Text.Json.JsonSerializer.Deserialize(token, _configuration.Key.Property.PropertyType);

    private static List<Func<TEntity, bool>> Compile(IReadOnlyList<LambdaExpression> residual) =>
        residual.Select(predicate => ((Expression<Func<TEntity, bool>>)predicate).Compile()).ToList();

    /// <summary>The first-segment members the map reads from the entity, or null when it needs the whole entity.</summary>
    private static IReadOnlyCollection<string>? MapColumns(LambdaExpression map)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return ColumnCollector.TryCollect(map, columns) ? columns : null;
    }

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

    /// <summary>Collects the first-segment members a lambda reads from its parameter; false when it needs the whole entity.</summary>
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
