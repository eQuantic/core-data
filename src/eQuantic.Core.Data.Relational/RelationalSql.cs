using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using eQuantic.Core.Data.Query;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Linq.Expressions.Nodes;

namespace eQuantic.Core.Data.Relational;

/// <summary>Relational query-option opt-ins.</summary>
public static class RelationalQueryOptionsExtensions
{
    private static readonly ConditionalWeakTable<object, object> ClientEvaluationOptIns = new();

    /// <summary>
    ///     Opts this query into client-side evaluation of the (rare) clauses SQL cannot express — arbitrary
    ///     predicates the interpreter does not model, or dialect-less collection/tuple shapes. The pushed-down part
    ///     runs on the database and the residual filters the fetched rows; the fetch is a superset of the result.
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

    internal static bool IsClientEvaluationOptedIn(object? options) =>
        options is not null && ClientEvaluationOptIns.TryGetValue(options, out _);
}

/// <summary>
///     The pushdown plan for one relational read: the parameterized <c>WHERE</c> the database executes and the
///     residual conjuncts the library evaluates client-side (rare — SQL is a complete target for the model).
/// </summary>
/// <param name="Where">The pushed-down predicate (empty when nothing pushes down).</param>
/// <param name="Parameters">The bound values, in <c>@p0…</c> order.</param>
/// <param name="Residual">The conjuncts SQL cannot express, evaluated client-side over the fetched rows.</param>
internal sealed record RelationalSqlPlan(string Where, List<object?> Parameters, IReadOnlyList<LambdaExpression> Residual)
{
    /// <summary>Human-readable description of the residual conjunction (empty when fully pushed down).</summary>
    public string ResidualText => string.Join(" AND ", Residual.Select(residual => residual.Body.ToString()));
}

/// <summary>Builds SQL predicates and pushdown plans from a <see cref="QueryOptions{TEntity}" />.</summary>
internal static class RelationalSql
{
    /// <summary>
    ///     Splits the filters into the pushdown plan. The whole tree is tried first — SQL renders
    ///     <c>OR</c>/<c>NOT</c>/<c>!=</c> natively, so the common case is a single shot; only when a shape refuses
    ///     does the conjunct-level split run, sending the refused conjuncts to client-side residual.
    /// </summary>
    public static RelationalSqlPlan Plan<TEntity>(SqlDialect dialect, RelationalEntityConfiguration configuration,
        QueryOptions<TEntity>? options, Expression<Func<TEntity, bool>>? extraFilter = null,
        Expression<Func<TEntity, bool>>? globalFilter = null, RelationalRelations? relations = null)
        where TEntity : class
    {
        var clauses = new List<string>();
        var parameters = new List<object?>();
        var residual = new List<LambdaExpression>();

        foreach (var filter in Filters(options, extraFilter, globalFilter))
        {
            var lambda = FilterInterpreter.ToNode(Prepared(filter, relations));

            if (TryInterpret(lambda.Body, out var whole)
                && SqlFilterRenderer.TryRender(dialect, configuration, whole, parameters, out var sql, relations))
            {
                clauses.Add(sql);
                continue;
            }

            foreach (var conjunct in NodePredicates.Conjuncts(lambda.Body))
            {
                if (TryInterpret(conjunct, out var interpreted)
                    && SqlFilterRenderer.TryRender(dialect, configuration, interpreted, parameters, out var conjunctSql, relations))
                {
                    clauses.Add(conjunctSql);
                }
                else if (CrossesRelation(conjunct))
                {
                    // A relation runs on the store or not at all: say why it cannot, rather than defer it client-side.
                    SqlFilterRenderer.Render(dialect, configuration, FilterInterpreter.Interpret(conjunct), parameters, relations);
                }
                else
                {
                    residual.Add(FilterInterpreter.RebuildPredicate<TEntity>(lambda, conjunct));
                }
            }
        }

        return new RelationalSqlPlan(string.Join(" AND ", clauses), parameters, residual);
    }

    /// <summary>
    ///     The filter as the renderer reads it: shapes a newer compiler gives normalized, and each test across a relation
    ///     marked for a correlated <c>EXISTS</c> (when the statement knows the model's relations).
    /// </summary>
    private static Expression<Func<TEntity, bool>> Prepared<TEntity>(Expression<Func<TEntity, bool>> filter, RelationalRelations? relations)
    {
        var normalized = PredicateNormalizer.Normalize(filter);
        return relations is null ? normalized : RelationPredicates.Mark(normalized, relations.Navigation);
    }

    /// <summary>Whether a conjunct holds a relation's marker (under any AND, OR or NOT).</summary>
    private static bool CrossesRelation(ExpressionNode node) => node switch
    {
        MethodCallNode { Method.Name: RelationPredicates.RelatedRowExistsFunction } => true,
        BinaryNode binary => CrossesRelation(binary.Left) || CrossesRelation(binary.Right),
        UnaryNode { Operand: { } operand } => CrossesRelation(operand),
        _ => false,
    };

    /// <summary>The relation tests of the filters, for a plan's notes.</summary>
    public static IReadOnlyList<RelationPredicate> Relations<TEntity>(QueryOptions<TEntity>? options,
        Expression<Func<TEntity, bool>>? extraFilter, Expression<Func<TEntity, bool>>? globalFilter, RelationalRelations relations)
        where TEntity : class =>
        Filters(options, extraFilter, globalFilter)
            .SelectMany(filter => RelationPredicates.Find(PredicateNormalizer.Normalize(filter), relations.Navigation))
            .ToList();

    /// <summary>
    ///     Renders the filters strictly — every clause must be expressible, or the refusal propagates. Set-based
    ///     writes use this: a <c>DELETE</c>/<c>UPDATE</c> cannot half-apply a predicate.
    /// </summary>
    public static (string Where, List<object?> Parameters) Where<TEntity>(SqlDialect dialect,
        RelationalEntityConfiguration configuration, Expression<Func<TEntity, bool>> filter,
        Expression<Func<TEntity, bool>>? globalFilter = null, RelationalRelations? relations = null)
        where TEntity : class
    {
        var clauses = new List<string>();
        var parameters = new List<object?>();

        foreach (var predicate in Filters(null, filter, globalFilter))
        {
            clauses.Add(SqlFilterRenderer.Render(dialect, configuration, FilterInterpreter.Interpret(Prepared(predicate, relations)),
                parameters, relations));
        }

        return (string.Join(" AND ", clauses), parameters);
    }

    /// <summary>
    ///     Renders the filters strictly into a statement whose other parts already bound values: the predicate's values
    ///     are appended to <paramref name="parameters" />, numbered after them.
    /// </summary>
    public static string Where<TEntity>(SqlDialect dialect, RelationalEntityConfiguration configuration,
        Expression<Func<TEntity, bool>>? filter, Expression<Func<TEntity, bool>>? globalFilter, List<object?> parameters,
        RelationalRelations? relations = null)
        where TEntity : class
    {
        var clauses = Filters(null, filter, globalFilter)
            .Select(predicate => SqlFilterRenderer.Render(dialect, configuration,
                FilterInterpreter.Interpret(Prepared(predicate, relations)), parameters, relations))
            .ToList();
        return clauses.Count == 0 ? "1 = 1" : string.Join(" AND ", clauses);
    }

    private static bool TryInterpret(ExpressionNode body, out QueryFilter filter)
    {
        try
        {
            filter = FilterInterpreter.Interpret(body);
            return true;
        }
        catch (NotSupportedException)
        {
            filter = null!;
            return false;
        }
    }

    private static IEnumerable<Expression<Func<TEntity, bool>>> Filters<TEntity>(
        QueryOptions<TEntity>? options, Expression<Func<TEntity, bool>>? extraFilter,
        Expression<Func<TEntity, bool>>? globalFilter) where TEntity : class
    {
        if (globalFilter is not null)
        {
            yield return globalFilter;
        }

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
