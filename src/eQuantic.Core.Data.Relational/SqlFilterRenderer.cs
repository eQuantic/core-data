using eQuantic.Core.Data.Query;

namespace eQuantic.Core.Data.Relational;

/// <summary>
///     Renders the dialect-agnostic <see cref="QueryFilter" /> model into a parameterized SQL predicate. SQL is a
///     complete target for the model — <c>AND</c>/<c>OR</c>/<c>NOT</c>, <c>&lt;&gt;</c> and <c>NULL</c> are native —
///     so, unlike CQL, almost every filter pushes down whole. C# semantics are preserved where SQL's three-valued
///     logic would diverge: <c>== null</c> renders <c>IS NULL</c>, and an <c>IN</c> whose values include
///     <c>null</c> renders <c>(col IN (…) OR col IS NULL)</c> — exactly what <c>list.Contains(x.Col)</c> means.
/// </summary>
internal static class SqlFilterRenderer
{
    /// <summary>Renders the filter, appending bound values to <paramref name="parameters" />.</summary>
    public static string Render(SqlDialect dialect, RelationalEntityConfiguration configuration, QueryFilter filter,
        List<object?> parameters)
    {
        return Visit(filter);

        string Visit(QueryFilter node) => node switch
        {
            LogicalFilter { Operator: LogicalOperator.And } and =>
                "(" + string.Join(" AND ", and.Operands.Select(Visit)) + ")",
            LogicalFilter { Operator: LogicalOperator.Or } or =>
                "(" + string.Join(" OR ", or.Operands.Select(Visit)) + ")",
            LogicalFilter { Operator: LogicalOperator.Not, Operands: [var operand] } =>
                "NOT (" + Visit(operand) + ")",
            ComparisonFilter comparison => Comparison(comparison),
            InFilter inFilter => In(inFilter),
            CollectionFilter collection => dialect.CollectionContains(Column(collection.Member), Bind(collection.Value), collection.Key),
            TupleComparisonFilter tuple => dialect.TupleComparison(
                tuple.Members.Select(Column).ToList(), tuple.Operator, tuple.Values.Select(Bind).ToList()),
            _ => throw new NotSupportedException($"Cannot render the filter '{node.GetType().Name}' to SQL."),
        };

        string Comparison(ComparisonFilter comparison)
        {
            var column = Column(comparison.Member);
            if (comparison.Value is null)
            {
                return comparison.Operator switch
                {
                    ComparisonOperator.Equal => $"{column} IS NULL",
                    ComparisonOperator.NotEqual => $"{column} IS NOT NULL",
                    _ => throw new NotSupportedException(
                        $"An ordered comparison of '{comparison.Member}' to NULL has no defined result."),
                };
            }

            // C# != matches NULL rows too; SQL <> filters them out — keep the C# semantics explicit.
            return comparison.Operator == ComparisonOperator.NotEqual
                ? $"({column} <> {Bind(comparison.Value)} OR {column} IS NULL)"
                : $"{column} {Operator(comparison.Operator)} {Bind(comparison.Value)}";
        }

        string In(InFilter inFilter)
        {
            var values = inFilter.Values.Where(value => value is not null).ToList();
            var hasNull = inFilter.Values.Count != values.Count;
            var column = Column(inFilter.Member);

            if (values.Count == 0)
            {
                return hasNull ? $"{column} IS NULL" : dialect.FalseLiteral;
            }

            var list = $"{column} IN ({string.Join(", ", values.Select(Bind))})";
            return hasNull ? $"({list} OR {column} IS NULL)" : list;
        }

        string Column(string member) =>
            dialect.Quote((configuration.ColumnFor(member)
                           ?? throw new NotSupportedException($"'{configuration.EntityType.Name}' has no mapped member '{member}'.")).Name);

        string Bind(object? value)
        {
            parameters.Add(dialect.BindValue(value));
            return "@p" + (parameters.Count - 1);
        }
    }

    /// <summary>Attempts to render, reporting failure instead of throwing — the engine turns refusals into residual work.</summary>
    public static bool TryRender(SqlDialect dialect, RelationalEntityConfiguration configuration, QueryFilter filter,
        List<object?> parameters, out string sql)
    {
        var checkpoint = parameters.Count;
        try
        {
            sql = Render(dialect, configuration, filter, parameters);
            return true;
        }
        catch (NotSupportedException)
        {
            parameters.RemoveRange(checkpoint, parameters.Count - checkpoint);
            sql = string.Empty;
            return false;
        }
    }

    private static string Operator(ComparisonOperator op) => op switch
    {
        ComparisonOperator.Equal => "=",
        ComparisonOperator.NotEqual => "<>",
        ComparisonOperator.GreaterThan => ">",
        ComparisonOperator.GreaterThanOrEqual => ">=",
        ComparisonOperator.LessThan => "<",
        ComparisonOperator.LessThanOrEqual => "<=",
        _ => throw new NotSupportedException($"The operator '{op}' is not expressible in SQL."),
    };
}
