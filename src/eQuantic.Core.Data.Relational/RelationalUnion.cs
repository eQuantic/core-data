using System.Linq.Expressions;
using eQuantic.Core.Data.Query;

namespace eQuantic.Core.Data.Relational;

/// <summary>
///     Renders a composed <see cref="UnionQuery{TResult}" /> into one <c>UNION</c>/<c>UNION ALL</c> statement:
///     one aliased <c>SELECT</c> per branch (every branch reordered into the first branch's shape), each branch's
///     filters — the entity's global filter included unless opted out — rendered <b>strictly</b> into its
///     <c>WHERE</c> (a union cannot run part of a branch client-side), and the combined ordering and paging
///     applied to the union result.
/// </summary>
internal static class RelationalUnion
{
    public static (string Sql, List<object?> Parameters, UnionProjection Shape) Build<TResult>(
        SqlDialect dialect, RelationalModel model, UnionQuery<TResult> query, Func<Type, LambdaExpression?> globalFilter)
    {
        var parameters = new List<object?>();
        var selects = new List<string>();
        var projections = UnionInterpreter.InterpretAll(query.Branches);
        var shape = projections[0];
        IReadOnlyList<string> targets = shape.Bindings.Select(binding => binding.Target).ToList();

        for (var index = 0; index < query.Branches.Count; index++)
        {
            var branch = query.Branches[index];
            var configuration = model.For(branch.EntityType);
            var projection = projections[index];

            var columns = projection.Bindings.Select(binding => binding switch
            {
                UnionColumnBinding column =>
                    $"{dialect.Quote(Column(configuration, branch, column, index).Name)} AS {dialect.Quote(binding.Target)}",
                UnionConstantBinding constant => $"{Bind(constant.Value)} AS {dialect.Quote(binding.Target)}",
                _ => throw new NotSupportedException($"Unknown union binding '{binding.GetType().Name}'."),
            });

            selects.Add($"SELECT {string.Join(", ", columns)} FROM {dialect.Quote(configuration.TableName)}"
                        + Where(dialect, configuration, branch, globalFilter, parameters, index));
        }

        var sql = string.Join(query.All ? " UNION ALL " : " UNION ", selects);

        if (query.Order.Count > 0)
        {
            sql += " ORDER BY " + string.Join(", ", query.Order.Select(order =>
                targets.Contains(order.Member, StringComparer.OrdinalIgnoreCase)
                    ? $"{dialect.Quote(order.Member)}{(order.Descending ? " DESC" : string.Empty)}"
                    : throw new NotSupportedException($"The union projects no member '{order.Member}' to order by.")));
        }
        else if (query.Limit is not null && dialect.RequiresOrderByForLimit)
        {
            throw new NotSupportedException(
                "This dialect pages with OFFSET/FETCH, which requires an ORDER BY — add OrderBy(...) to the union.");
        }

        if (query.Offset is not null && query.Limit is null)
        {
            throw new NotSupportedException("Skip without Take is not supported on a union; add Take(...).");
        }

        if (query.Limit is { } limit)
        {
            parameters.Add(limit);
            var limitParameter = "@p" + (parameters.Count - 1);
            string? offsetParameter = null;
            if (query.Offset is { } offset)
            {
                parameters.Add(offset);
                offsetParameter = "@p" + (parameters.Count - 1);
            }

            sql += " " + dialect.LimitClause(limitParameter, offsetParameter);
        }

        return (sql, parameters, shape);

        string Bind(object? value)
        {
            parameters.Add(dialect.BindValue(value));
            return "@p" + (parameters.Count - 1);
        }
    }

    private static RelationalColumn Column(RelationalEntityConfiguration configuration, UnionBranch branch,
        UnionColumnBinding column, int index) =>
        configuration.ColumnFor(column.Member)
        ?? throw new NotSupportedException(
            $"'{branch.EntityType.Name}' has no mapped member '{column.Member}' to project (union branch {index + 1}).");

    private static string Where(SqlDialect dialect, RelationalEntityConfiguration configuration, UnionBranch branch,
        Func<Type, LambdaExpression?> globalFilter, List<object?> parameters, int index)
    {
        var filters = new List<LambdaExpression>();
        if (!branch.IgnoreQueryFilters && globalFilter(branch.EntityType) is { } global)
        {
            filters.Add(global);
        }

        filters.AddRange(branch.Filters);

        var clauses = new List<string>();
        foreach (var filter in filters)
        {
            try
            {
                clauses.Add(SqlFilterRenderer.Render(dialect, configuration, FilterInterpreter.Interpret(filter), parameters));
            }
            catch (NotSupportedException inner)
            {
                throw new NotSupportedException(
                    $"Union branch {index + 1} ({branch.EntityType.Name}) has a filter SQL cannot express — a union " +
                    $"cannot run part of a branch client-side; restructure the filter or run the branches as separate " +
                    $"reads. {inner.Message}", inner);
            }
        }

        return clauses.Count > 0 ? " WHERE " + string.Join(" AND ", clauses) : string.Empty;
    }

}
