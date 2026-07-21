using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using eQuantic.Core.Data.Query;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Linq.Expressions.Nodes;

namespace eQuantic.Core.Data.Cassandra;

/// <summary>Cassandra-specific query-option opt-ins. Every capability beyond the native access path is explicit:
/// <see cref="AllowFiltering{TEntity}" /> acknowledges a server-side scan, <see cref="AllowClientEvaluation{TEntity}" />
/// acknowledges client-side residual filtering. The default (no opt-in) rejects both, so costs never ship silently.</summary>
public static class CassandraQueryOptionsExtensions
{
    // Opt-ins are tracked per options instance so the diagnostic Tag stays free for the caller's own use.
    private static readonly ConditionalWeakTable<object, object> FilteringOptIns = new();
    private static readonly ConditionalWeakTable<object, object> ClientEvaluationOptIns = new();

    /// <summary>
    ///     Opts this query into <c>ALLOW FILTERING</c>, letting it filter on non-key columns. This is a scan —
    ///     use it deliberately; the default (without this) rejects non-key filters so accidental scans do not ship.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="options">The query options.</param>
    /// <returns>The same options for chaining.</returns>
    public static QueryOptions<TEntity> AllowFiltering<TEntity>(this QueryOptions<TEntity> options)
        where TEntity : class
    {
        FilteringOptIns.GetValue(options, static _ => new object());
        return options;
    }

    /// <summary>
    ///     Opts this query into client-side evaluation of the filter clauses CQL cannot express (<c>OR</c> across
    ///     columns, <c>!=</c>, <c>NULL</c> comparisons, arbitrary predicates): the pushed-down part runs on the
    ///     cluster and the residual runs over the fetched rows. The fetch is a superset of the result — combine
    ///     with a partition-scoped filter to keep it bounded; an unscoped residual additionally requires
    ///     <see cref="AllowFiltering{TEntity}" /> because it scans the table.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="options">The query options.</param>
    /// <returns>The same options for chaining.</returns>
    public static QueryOptions<TEntity> AllowClientEvaluation<TEntity>(this QueryOptions<TEntity> options)
        where TEntity : class
    {
        ClientEvaluationOptIns.GetValue(options, static _ => new object());
        return options;
    }

    internal static bool IsAllowFilteringOptedIn(object? options) =>
        options is not null && FilteringOptIns.TryGetValue(options, out _);

    internal static bool IsClientEvaluationOptedIn(object? options) =>
        options is not null && ClientEvaluationOptIns.TryGetValue(options, out _);
}

/// <summary>One branch of an OR-split query: its rendered <c>WHERE</c> fragment and bound values.</summary>
/// <param name="Where">The branch's <c>WHERE</c> fragment (ANDed with the plan's common conjunction).</param>
/// <param name="Values">The branch's bound values, in order.</param>
internal sealed record CassandraCqlAlternative(string Where, object?[] Values);

/// <summary>
///     The pushdown plan for one Cassandra read: the CQL <c>WHERE</c> conjunction the cluster executes, the
///     OR branches that run as parallel single-partition queries (merged and de-duplicated client-side), the
///     residual conjuncts the library evaluates client-side, and the cost facts a caller (or a plan explain)
///     needs to reason about it.
/// </summary>
/// <param name="Where">The pushed-down <c>WHERE</c> conjunction (empty when nothing pushes down).</param>
/// <param name="Values">The bound values of the pushed clauses, in order.</param>
/// <param name="RequiresAllowFiltering">Whether the pushed clauses need CQL <c>ALLOW FILTERING</c>.</param>
/// <param name="Residual">The conjuncts CQL cannot express, to be evaluated client-side over the fetched rows.</param>
/// <param name="Alternatives">The OR branches, each a native partition-pinned query (empty when there is no split).</param>
/// <param name="PartitionScoped">Whether a pushed clause (or every OR branch) pins the partition.</param>
internal sealed record CassandraCqlPlan(
    string Where,
    object?[] Values,
    bool RequiresAllowFiltering,
    IReadOnlyList<LambdaExpression> Residual,
    IReadOnlyList<CassandraCqlAlternative> Alternatives,
    bool PartitionScoped)
{
    /// <summary>Human-readable description of the residual conjunction (empty when fully pushed down).</summary>
    public string ResidualText => string.Join(" AND ", Residual.Select(residual => residual.Body.ToString()));
}

/// <summary>Builds CQL <c>WHERE</c> fragments and pushdown plans from a <see cref="QueryOptions{TEntity}" />.</summary>
internal static class CassandraCql
{
    /// <summary>
    ///     Splits the options' filters — and an optional extra predicate (a <c>GetFiltered</c> argument or an id
    ///     lookup) — into the pushdown plan: each top-level conjunct is rendered to CQL when the dialect can express
    ///     it, and becomes client-side residual work when the strict renderer refuses it. The caller's options are
    ///     never mutated.
    /// </summary>
    public static CassandraCqlPlan Plan<TEntity>(CassandraEntityConfiguration configuration,
        QueryOptions<TEntity>? options, Expression<Func<TEntity, bool>>? extraFilter = null)
        where TEntity : class
    {
        var clauses = new List<string>();
        var values = new List<object?>();
        var residual = new List<LambdaExpression>();
        var alternatives = new List<CassandraCqlAlternative>();
        var requiresFiltering = false;
        var partitionScoped = false;

        foreach (var filter in Filters(options, extraFilter))
        {
            // One node-model conversion per filter (a single partial-evaluation pass); splitting, interpretation
            // and the OR analysis all walk the nodes, and only a refused conjunct is rebuilt into an expression
            // for client-side residual evaluation.
            var lambda = FilterInterpreter.ToNode(filter);
            foreach (var conjunct in NodePredicates.Conjuncts(lambda.Body))
            {
                QueryFilter interpreted;
                try
                {
                    interpreted = FilterInterpreter.Interpret(conjunct);
                }
                catch (NotSupportedException)
                {
                    // The interpreter does not model this shape (an arbitrary predicate): whole conjunct residual.
                    residual.Add(FilterInterpreter.RebuildPredicate<TEntity>(lambda, conjunct));
                    continue;
                }

                if (CassandraCqlRenderer.TryRender(configuration, interpreted, out var rendered))
                {
                    if (rendered.Cql.Length > 0)
                    {
                        clauses.Add(rendered.Cql);
                        values.AddRange(rendered.Values);
                        requiresFiltering |= rendered.RequiresAllowFiltering;
                        partitionScoped |= CassandraCqlRenderer.PinsPartition(configuration, interpreted);
                    }
                }
                else if (alternatives.Count == 0 && TryOrSplit(configuration, conjunct, alternatives))
                {
                    // An OR whose every branch is native and partition-pinned runs as parallel split queries
                    // ("one query per access path"), merged and de-duplicated client-side. One split per query.
                    partitionScoped = true;
                }
                else
                {
                    // CQL cannot express it (OR across columns, !=, NULL, …): the refusal becomes residual work.
                    residual.Add(FilterInterpreter.RebuildPredicate<TEntity>(lambda, conjunct));
                }
            }
        }

        return new CassandraCqlPlan(string.Join(" AND ", clauses), values.ToArray(), requiresFiltering, residual, alternatives, partitionScoped);
    }

    /// <summary>
    ///     Attempts to split an OR conjunct into native branches: every disjunct must render (no
    ///     <c>ALLOW FILTERING</c>) and pin the partition — otherwise the split would multiply scans instead of
    ///     multiplying cheap point paths, and the conjunct stays residual.
    /// </summary>
    private static bool TryOrSplit(CassandraEntityConfiguration configuration,
        ExpressionNode conjunct, List<CassandraCqlAlternative> alternatives)
    {
        var branches = NodePredicates.Disjuncts(conjunct);
        if (branches.Count < 2)
        {
            return false;
        }

        var rendered = new List<CassandraCqlAlternative>(branches.Count);
        foreach (var branch in branches)
        {
            QueryFilter interpreted;
            try
            {
                interpreted = FilterInterpreter.Interpret(branch);
            }
            catch (NotSupportedException)
            {
                return false;
            }

            if (!CassandraCqlRenderer.TryRender(configuration, interpreted, out var cql)
                || cql.Cql.Length == 0
                || cql.RequiresAllowFiltering
                || !CassandraCqlRenderer.PinsPartition(configuration, interpreted))
            {
                return false;
            }

            rendered.Add(new CassandraCqlAlternative(cql.Cql, cql.Values));
        }

        alternatives.AddRange(rendered);
        return true;
    }

    /// <summary>
    ///     Renders the filters strictly — every clause must be expressible in CQL, or the renderer's
    ///     <see cref="NotSupportedException" /> propagates. Set-based writes use this: a <c>DELETE</c>/<c>UPDATE</c>
    ///     cannot half-apply a predicate, so nothing may fall to residual.
    /// </summary>
    public static (string Where, object?[] Values, bool RequiresFiltering) Where<TEntity>(
        CassandraEntityConfiguration configuration, QueryOptions<TEntity>? options,
        Expression<Func<TEntity, bool>>? extraFilter = null)
        where TEntity : class
    {
        var clauses = new List<string>();
        var values = new List<object?>();
        var requiresFiltering = false;

        foreach (var filter in Filters(options, extraFilter))
        {
            var (cql, filterValues, allowFiltering) = CassandraCqlRenderer.Render(configuration, FilterInterpreter.Interpret(filter));
            if (cql.Length == 0)
            {
                continue;
            }

            clauses.Add(cql);
            values.AddRange(filterValues);
            requiresFiltering |= allowFiltering;
        }

        return (string.Join(" AND ", clauses), values.ToArray(), requiresFiltering);
    }

    public static bool AllowFilteringOptedIn<TEntity>(QueryOptions<TEntity>? options) where TEntity : class =>
        CassandraQueryOptionsExtensions.IsAllowFilteringOptedIn(options);

    public static bool ClientEvaluationOptedIn<TEntity>(QueryOptions<TEntity>? options) where TEntity : class =>
        CassandraQueryOptionsExtensions.IsClientEvaluationOptedIn(options);

    private static IEnumerable<Expression<Func<TEntity, bool>>> Filters<TEntity>(
        QueryOptions<TEntity>? options, Expression<Func<TEntity, bool>>? extraFilter) where TEntity : class
    {
        if (options?.Filter is not null)
        {
            yield return options.Filter;
        }

        if (options?.Specification is not null)
        {
            yield return options.Specification.SatisfiedBy();
        }

        if (extraFilter is not null)
        {
            yield return extraFilter;
        }
    }
}
