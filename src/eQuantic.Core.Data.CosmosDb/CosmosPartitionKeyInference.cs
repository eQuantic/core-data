using System.Globalization;
using System.Linq.Expressions;
using eQuantic.Linq.Expressions;
using eQuantic.Linq.Expressions.Nodes;
using Microsoft.Azure.Cosmos;

namespace eQuantic.Core.Data.CosmosDb;

/// <summary>
///     Infers the partition key of a query from its filter. When a filter pins the partition-key field to a
///     value through an AND-reachable equality (e.g. <c>x =&gt; x.Category == "Books" &amp;&amp; …</c>), the query
///     can be scoped to that single partition instead of fanning out across all of them — the single biggest RU
///     saving in Cosmos. The analysis runs over the eQuantic.Linq.Expressions AST, which partial-evaluates
///     captured variables to constants, so <c>x =&gt; x.Category == category</c> is recognised too.
/// </summary>
internal static class CosmosPartitionKeyInference
{
    /// <summary>Infers the partition key pinned by the filters, or <c>null</c> when none scope a single partition.</summary>
    /// <param name="partitionKeyPath">The container's partition key path (e.g. <c>/category</c>).</param>
    /// <param name="filters">The candidate filters (predicate and/or specification body).</param>
    public static PartitionKey? Infer(string partitionKeyPath, params Expression?[] filters)
    {
        var field = partitionKeyPath.TrimStart('/');
        if (field.Length == 0 || field.Contains('/'))
        {
            // Hierarchical partition keys are not inferred (yet); fall back to a cross-partition query.
            return null;
        }

        foreach (var filter in filters)
        {
            if (filter is null)
            {
                continue;
            }

            var entityType = (filter as LambdaExpression)?.Parameters is [{ } parameter, ..] ? parameter.Type : null;
            var node = ExpressionSerializer.Default.ToNode(filter);
            var body = node is LambdaNode lambda ? lambda.Body : node;
            if (TryFind(body, entityType, field, out var value))
            {
                return ToPartitionKey(value);
            }
        }

        return null;
    }

    private static bool TryFind(ExpressionNode node, Type? entityType, string field, out object? value)
    {
        value = null;
        if (node is not BinaryNode binary)
        {
            return false;
        }

        if (binary.NodeType is ExpressionType.AndAlso or ExpressionType.And)
        {
            return TryFind(binary.Left, entityType, field, out value) || TryFind(binary.Right, entityType, field, out value);
        }

        if (binary.NodeType != ExpressionType.Equal)
        {
            return false;
        }

        var left = Unwrap(binary.Left);
        var right = Unwrap(binary.Right);

        if (IsPartitionMember(left, entityType, field) && right is ConstantNode rightConstant)
        {
            value = rightConstant.Value;
            return true;
        }

        if (IsPartitionMember(right, entityType, field) && left is ConstantNode leftConstant)
        {
            value = leftConstant.Value;
            return true;
        }

        return false;
    }

    private static bool IsPartitionMember(ExpressionNode node, Type? entityType, string field)
    {
        if (node is not MemberNode { Expression: ParameterNode } member)
        {
            return false;
        }

        // The stored name resolves through the same contract as the serializer ([StoredAs] > JsonPropertyName >
        // camelCase); without a reachable property the camelCase convention is the only candidate.
        var property = entityType?.GetProperty(member.Member.Name,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        var storedName = property is null ? CosmosNaming.CamelCase(member.Member.Name) : CosmosNaming.StoredName(property);
        return storedName == field;
    }

    private static ExpressionNode Unwrap(ExpressionNode node) =>
        node is UnaryNode { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked, Operand: { } operand }
            ? Unwrap(operand)
            : node;

    private static PartitionKey ToPartitionKey(object? value) => value switch
    {
        null => PartitionKey.Null,
        string text => new PartitionKey(text),
        bool flag => new PartitionKey(flag),
        _ => new PartitionKey(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
    };
}
