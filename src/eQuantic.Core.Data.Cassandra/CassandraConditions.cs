using System.Linq.Expressions;
using eQuantic.Core.Data.Query;

namespace eQuantic.Core.Data.Cassandra;

/// <summary>
///     Splits a set-based write's filter the way a lightweight transaction needs it: the conjuncts on the primary key
///     find the rows (a <c>WHERE</c> the table serves without <c>ALLOW FILTERING</c>), and every other conjunct becomes
///     the transaction's condition (<c>IF col = ? AND col &gt; ?</c>), which Cassandra checks and applies in one Paxos
///     round per row — so a filter on the key and other members is a compare-and-set.
/// </summary>
/// <param name="KeyWhere">The CQL restricting the primary key (empty when the filter names none of it).</param>
/// <param name="KeyValues">Its bound values.</param>
/// <param name="PinsPartition">Whether the key restriction names the partition (a write may only run per partition).</param>
/// <param name="Conditions">The <c>IF</c> conditions, each with its bound values.</param>
internal sealed record CassandraConditions(string KeyWhere, object?[] KeyValues, bool PinsPartition,
    IReadOnlyList<(string Cql, object?[] Values)> Conditions)
{
    /// <summary>Splits the filter (and the global filter) into the key restriction and the conditions.</summary>
    public static CassandraConditions Split<TEntity>(CassandraEntityConfiguration configuration,
        Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, bool>>? globalFilter)
        where TEntity : class
    {
        var keyClauses = new List<string>();
        var keyValues = new List<object?>();
        var conditions = new List<(string Cql, object?[] Values)>();
        var pinsPartition = false;

        foreach (var predicate in new[] { globalFilter, filter }.Where(predicate => predicate is not null))
        {
            foreach (var conjunct in Conjuncts(FilterInterpreter.Interpret(predicate!)))
            {
                // A key column never goes into a condition (Cassandra refuses IF on the primary key): a restriction the
                // table cannot serve on its own is caught by the partition check before anything is written.
                if (OnKeyOnly(configuration, conjunct)
                    && CassandraCqlRenderer.TryRender(configuration, conjunct, out var rendered))
                {
                    keyClauses.Add(rendered.Cql);
                    keyValues.AddRange(rendered.Values);
                    pinsPartition |= CassandraCqlRenderer.PinsPartition(configuration, conjunct);
                    continue;
                }

                conditions.Add(Condition(configuration, conjunct));
            }
        }

        return new CassandraConditions(string.Join(" AND ", keyClauses), keyValues.ToArray(), pinsPartition, conditions);
    }

    private static IEnumerable<QueryFilter> Conjuncts(QueryFilter filter) =>
        filter is LogicalFilter { Operator: LogicalOperator.And } and
            ? and.Operands.SelectMany(Conjuncts)
            : [filter];

    private static bool OnKeyOnly(CassandraEntityConfiguration configuration, QueryFilter filter) => filter switch
    {
        ComparisonFilter comparison => configuration.IsKey(configuration.ColumnFor(comparison.Member)),
        InFilter inFilter => configuration.IsKey(configuration.ColumnFor(inFilter.Member)),
        TupleComparisonFilter tuple => tuple.Members.All(member => configuration.IsKey(configuration.ColumnFor(member))),
        _ => false,
    };

    /// <summary>Renders a conjunct as a lightweight transaction's condition; a shape <c>IF</c> cannot express is refused.</summary>
    private static (string Cql, object?[] Values) Condition(CassandraEntityConfiguration configuration, QueryFilter filter)
    {
        switch (filter)
        {
            case ComparisonFilter comparison:
            {
                var column = configuration.ColumnFor(comparison.Member);
                var stored = CassandraValues.ToStored(configuration.Column(column), comparison.Value);
                var op = comparison.Operator switch
                {
                    ComparisonOperator.Equal => "=",
                    ComparisonOperator.NotEqual => "!=",
                    ComparisonOperator.GreaterThan => ">",
                    ComparisonOperator.GreaterThanOrEqual => ">=",
                    ComparisonOperator.LessThan => "<",
                    ComparisonOperator.LessThanOrEqual => "<=",
                    _ => throw new NotSupportedException($"A lightweight transaction's condition has no '{comparison.Operator}'."),
                };

                // An unset column is null in a condition: IF col = null holds for it, as == null does in C#.
                return stored is null && op is "=" or "!="
                    ? ($"{column} {op} null", [])
                    : stored is null
                        ? throw new NotSupportedException($"An ordered comparison of '{comparison.Member}' to null has no defined result.")
                        : ($"{column} {op} ?", [stored]);
            }

            case InFilter inFilter when inFilter.Values.All(value => value is not null):
            {
                var column = configuration.ColumnFor(inFilter.Member);
                return ($"{column} IN ({string.Join(", ", inFilter.Values.Select(_ => "?"))})",
                    inFilter.Values.Select(value => CassandraValues.ToStored(configuration.Column(column), value)).ToArray());
            }

            default:
                throw new NotSupportedException(
                    $"A filter outside the primary key becomes a lightweight transaction's condition (IF …), which holds " +
                    $"comparisons (=, !=, <, <=, >, >=) and IN joined by AND; '{filter.GetType().Name}' is not one of them.");
        }
    }
}
