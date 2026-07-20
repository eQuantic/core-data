using System.Linq.Expressions;
using eQuantic.Linq.Expressions;
using eQuantic.Linq.Expressions.Nodes;

namespace eQuantic.Core.Data.Cassandra;

/// <summary>A translated CQL <c>WHERE</c> clause with its bound parameters.</summary>
/// <param name="Cql">The CQL predicate (without the <c>WHERE</c> keyword), or empty when there is no filter.</param>
/// <param name="Parameters">The bound values, in order.</param>
/// <param name="RequiresAllowFiltering">Whether any clause targets a non-key column (needs <c>ALLOW FILTERING</c>).</param>
internal sealed record CassandraWhere(string Cql, IReadOnlyList<object?> Parameters, bool RequiresAllowFiltering);

/// <summary>
///     Translates a typed filter into a CQL <c>WHERE</c> over the eQuantic.Linq.Expressions AST (partial-evaluating
///     captured variables). The hybrid policy: equality on the partition key and equality/range on clustering keys
///     translate natively; a predicate on any other column is allowed only under <c>ALLOW FILTERING</c> (flagged
///     here, opted into by the caller); <c>OR</c> and non-comparison shapes are rejected, matching CQL's limits.
/// </summary>
internal static class CassandraFilterTranslator
{
    public static CassandraWhere Translate(CassandraEntityConfiguration configuration, Expression filter)
    {
        var node = ExpressionSerializer.Default.ToNode(filter);
        var body = node is LambdaNode lambda ? lambda.Body : node;

        var clauses = new List<string>();
        var parameters = new List<object?>();
        var requiresFiltering = false;
        Walk(body, configuration, clauses, parameters, ref requiresFiltering);

        return new CassandraWhere(string.Join(" AND ", clauses), parameters, requiresFiltering);
    }

    private static void Walk(ExpressionNode node, CassandraEntityConfiguration configuration,
        List<string> clauses, List<object?> parameters, ref bool requiresFiltering)
    {
        if (node is not BinaryNode binary)
        {
            throw new NotSupportedException(
                $"A Cassandra filter must be column comparisons combined with AND; got '{node.GetType().Name}'.");
        }

        if (binary.NodeType is ExpressionType.AndAlso or ExpressionType.And)
        {
            Walk(binary.Left, configuration, clauses, parameters, ref requiresFiltering);
            Walk(binary.Right, configuration, clauses, parameters, ref requiresFiltering);
            return;
        }

        if (binary.NodeType is ExpressionType.OrElse or ExpressionType.Or)
        {
            throw new NotSupportedException(
                "Cassandra CQL has no OR in a WHERE clause; model the access pattern with the partition key instead.");
        }

        var (column, value, flip) = Resolve(binary);
        var op = Operator(binary.NodeType, flip);

        var isPartition = configuration.PartitionKeys.Any(key => CassandraEntityConfiguration.Same(key, column));
        var isClustering = configuration.IsClusteringKey(column);

        if (isPartition && op != "=")
        {
            throw new NotSupportedException($"The partition key '{column}' supports only equality in a Cassandra WHERE.");
        }

        if (!isPartition && !isClustering)
        {
            requiresFiltering = true;
        }

        clauses.Add($"{column} {op} ?");
        parameters.Add(value);
    }

    private static (string Column, object? Value, bool Flip) Resolve(BinaryNode binary)
    {
        var left = Unwrap(binary.Left);
        var right = Unwrap(binary.Right);

        if (left is MemberNode { Expression: ParameterNode } leftMember && right is ConstantNode rightConstant)
        {
            return (leftMember.Member.Name, rightConstant.Value, false);
        }

        if (right is MemberNode { Expression: ParameterNode } rightMember && left is ConstantNode leftConstant)
        {
            return (rightMember.Member.Name, leftConstant.Value, true);
        }

        throw new NotSupportedException("Each Cassandra filter clause must compare a column to a constant.");
    }

    private static string Operator(ExpressionType nodeType, bool flip) => nodeType switch
    {
        ExpressionType.Equal => "=",
        ExpressionType.GreaterThan => flip ? "<" : ">",
        ExpressionType.GreaterThanOrEqual => flip ? "<=" : ">=",
        ExpressionType.LessThan => flip ? ">" : "<",
        ExpressionType.LessThanOrEqual => flip ? ">=" : "<=",
        _ => throw new NotSupportedException($"The comparison '{nodeType}' is not supported in a Cassandra WHERE."),
    };

    private static ExpressionNode Unwrap(ExpressionNode node) =>
        node is UnaryNode { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked, Operand: { } operand }
            ? Unwrap(operand)
            : node;
}
