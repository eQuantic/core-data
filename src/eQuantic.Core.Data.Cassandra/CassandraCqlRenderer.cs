using eQuantic.Core.Data.Query;

namespace eQuantic.Core.Data.Cassandra;

/// <summary>
///     Renders a dialect-agnostic <see cref="QueryFilter" /> (produced by the core <see cref="FilterInterpreter" />)
///     into a CQL <c>WHERE</c>, applying the hybrid policy: equality/IN on the partition key and equality/range on
///     clustering keys render natively; a <b>range on the partition key</b> becomes <c>token(col) op token(?)</c>;
///     a predicate on any other column is allowed but flagged as needing <c>ALLOW FILTERING</c> (the caller opts
///     in); and shapes CQL cannot express (<c>OR</c> across columns, <c>NOT</c>, <c>&lt;&gt;</c>) are rejected.
/// </summary>
internal static class CassandraCqlRenderer
{
    public static (string Cql, object?[] Values, bool RequiresAllowFiltering) Render(CassandraEntityConfiguration configuration, QueryFilter filter)
    {
        var clauses = new List<string>();
        var values = new List<object?>();
        var requiresFiltering = false;

        Conjunction(filter, configuration, clauses, values, ref requiresFiltering);
        return (string.Join(" AND ", clauses), values.ToArray(), requiresFiltering);
    }

    private static void Conjunction(QueryFilter filter, CassandraEntityConfiguration configuration,
        List<string> clauses, List<object?> values, ref bool requiresFiltering)
    {
        switch (filter)
        {
            case LogicalFilter { Operator: LogicalOperator.And } and:
                foreach (var operand in and.Operands)
                {
                    Conjunction(operand, configuration, clauses, values, ref requiresFiltering);
                }

                return;
            case LogicalFilter { Operator: LogicalOperator.Or }:
                throw new NotSupportedException(
                    "Cassandra CQL has no OR across different columns in a WHERE; model the access pattern with the partition key.");
            case LogicalFilter { Operator: LogicalOperator.Not }:
                throw new NotSupportedException("Cassandra CQL has no NOT in a WHERE.");
            case ComparisonFilter comparison:
                clauses.Add(Comparison(comparison, configuration, values, ref requiresFiltering));
                return;
            case InFilter inFilter:
                clauses.Add(In(inFilter, configuration, values, ref requiresFiltering));
                return;
            case CollectionFilter collection:
                clauses.Add(Collection(collection, values, ref requiresFiltering));
                return;
            case TupleComparisonFilter tuple:
                clauses.Add(Tuple(tuple, configuration, values, ref requiresFiltering));
                return;
            default:
                throw new NotSupportedException($"Cannot render the filter '{filter.GetType().Name}' to CQL.");
        }
    }

    private static string Comparison(ComparisonFilter filter, CassandraEntityConfiguration configuration, List<object?> values, ref bool requiresFiltering)
    {
        var column = filter.Member;
        var isPartition = configuration.PartitionKeys.Any(key => CassandraEntityConfiguration.Same(key, column));

        if (filter.Operator is ComparisonOperator.NotEqual)
        {
            throw new NotSupportedException($"Cassandra CQL has no '<>' operator; '{column} <> ?' is not expressible.");
        }

        if (filter.Operator is ComparisonOperator.Equal)
        {
            if (!isPartition && !configuration.IsClusteringKey(column))
            {
                requiresFiltering = true;
            }

            values.Add(filter.Value);
            return $"{column} = ?";
        }

        // A range on the partition key is expressed through token(); ranges on clustering keys are native.
        if (isPartition)
        {
            values.Add(filter.Value);
            return $"token({column}) {Operator(filter.Operator)} token(?)";
        }

        if (!configuration.IsClusteringKey(column))
        {
            requiresFiltering = true;
        }

        values.Add(filter.Value);
        return $"{column} {Operator(filter.Operator)} ?";
    }

    private static string In(InFilter filter, CassandraEntityConfiguration configuration, List<object?> values, ref bool requiresFiltering)
    {
        if (!configuration.IsKey(filter.Member))
        {
            requiresFiltering = true;
        }

        values.AddRange(filter.Values);
        return $"{filter.Member} IN ({string.Join(", ", filter.Values.Select(_ => "?"))})";
    }

    private static string Collection(CollectionFilter filter, List<object?> values, ref bool requiresFiltering)
    {
        // CONTAINS / CONTAINS KEY need a secondary index or ALLOW FILTERING.
        requiresFiltering = true;
        values.Add(filter.Value);
        return filter.Key ? $"{filter.Member} CONTAINS KEY ?" : $"{filter.Member} CONTAINS ?";
    }

    private static string Tuple(TupleComparisonFilter filter, CassandraEntityConfiguration configuration, List<object?> values, ref bool requiresFiltering)
    {
        if (!filter.Members.All(configuration.IsClusteringKey))
        {
            requiresFiltering = true;
        }

        values.AddRange(filter.Values);
        return $"({string.Join(", ", filter.Members)}) {Operator(filter.Operator)} ({string.Join(", ", filter.Values.Select(_ => "?"))})";
    }

    private static string Operator(ComparisonOperator op) => op switch
    {
        ComparisonOperator.Equal => "=",
        ComparisonOperator.GreaterThan => ">",
        ComparisonOperator.GreaterThanOrEqual => ">=",
        ComparisonOperator.LessThan => "<",
        ComparisonOperator.LessThanOrEqual => "<=",
        _ => throw new NotSupportedException($"The operator '{op}' is not expressible in a Cassandra WHERE."),
    };
}
