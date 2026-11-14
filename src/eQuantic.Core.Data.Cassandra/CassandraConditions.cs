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
/// <param name="NamesEveryKey">Whether the key restriction names every column of the primary key (with <c>=</c> or <c>IN</c>).</param>
internal sealed record CassandraConditions(string KeyWhere, object?[] KeyValues, bool PinsPartition,
    IReadOnlyList<(string Cql, object?[] Values)> Conditions, bool NamesEveryKey = false)
{
    /// <summary>
    ///     The key restriction as the statements that run it carry it: itself, or — when its longest <c>IN</c> holds more
    ///     keys than one chunk (<see cref="CassandraCqlRenderer.InChunkSize" />) — one restriction per chunk of that <c>IN</c>,
    ///     each run by a statement of its own.
    /// </summary>
    public IReadOnlyList<(string Where, object?[] Values)> KeyChunks { get; init; } = [(KeyWhere, KeyValues)];

    /// <summary>Splits the filter (and the global filter) into the key restriction and the conditions.</summary>
    public static CassandraConditions Split<TEntity>(CassandraEntityConfiguration configuration,
        Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, bool>>? globalFilter)
        where TEntity : class
    {
        var keyClauses = new List<(string Cql, object?[] Values)>();
        var chunkable = new List<(int Clause, InFilter Filter)>();
        var conditions = new List<(string Cql, object?[] Values)>();
        var pinsPartition = false;
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var predicate in new[] { globalFilter, filter }.Where(predicate => predicate is not null))
        {
            foreach (var conjunct in Conjuncts(FilterInterpreter.Interpret(predicate!)))
            {
                // A key column never goes into a condition (Cassandra refuses IF on the primary key): a restriction the
                // table cannot serve on its own is caught by the partition check before anything is written.
                if (OnKeyOnly(configuration, conjunct)
                    && CassandraCqlRenderer.TryRender(configuration, conjunct, out var rendered))
                {
                    if (conjunct is InFilter chunked && CassandraCqlRenderer.IsChunkable(configuration, chunked))
                    {
                        chunkable.Add((keyClauses.Count, chunked));
                    }

                    keyClauses.Add((rendered.Cql, rendered.Values));
                    pinsPartition |= CassandraCqlRenderer.PinsPartition(configuration, conjunct);
                    if (conjunct is ComparisonFilter { Operator: ComparisonOperator.Equal } equal)
                    {
                        named.Add(configuration.ColumnFor(equal.Member));
                    }
                    else if (conjunct is InFilter inFilter)
                    {
                        named.Add(configuration.ColumnFor(inFilter.Member));
                    }

                    continue;
                }

                conditions.Add(Condition(configuration, conjunct));
            }
        }

        var namesEveryKey = CassandraMapper.PrimaryKey(configuration).All(named.Contains);
        var split = new CassandraConditions(string.Join(" AND ", keyClauses.Select(clause => clause.Cql)),
            keyClauses.SelectMany(clause => clause.Values).ToArray(), pinsPartition, conditions, namesEveryKey);
        if (chunkable.Count == 0)
        {
            return split;
        }

        var (index, longest) = chunkable.MaxBy(candidate => candidate.Filter.Values.Count);
        return split with
        {
            KeyChunks = CassandraCqlRenderer.InChunks(configuration, longest).Select(chunk =>
            {
                var clauses = keyClauses.Select((clause, position) => position == index ? chunk : clause).ToList();
                return (string.Join(" AND ", clauses.Select(clause => clause.Cql)), clauses.SelectMany(clause => clause.Values).ToArray());
            }).ToList(),
        };
    }

    /// <summary>The enum a member is stored as the labels of, or <c>null</c>.</summary>
    private static Type? LabelEnum(CassandraEntityConfiguration configuration, string member) =>
        configuration.Column(configuration.ColumnFor(member)) is { CqlType: "text" } column
        && (Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType) is { IsEnum: true } enumType
            ? enumType
            : null;

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
            // An enum stored as its labels orders by its members' values, as C# does: the condition is membership in the
            // labels whose member satisfies the comparison, where IF col >= ? would compare the labels' text.
            case ComparisonFilter { Value: not null } ordered when EnumOrder.IsOrdered(ordered.Operator)
                                                               && LabelEnum(configuration, ordered.Member) is { } labelEnum:
            {
                var satisfying = EnumOrder.Satisfying(labelEnum, ordered.Operator, ordered.Value);
                return satisfying.Count > 0
                    ? Condition(configuration, new InFilter(ordered.Member, satisfying.Cast<object?>().ToList()))
                    : throw new NotSupportedException(
                        $"No member of '{labelEnum.Name}' satisfies the comparison of '{ordered.Member}', so the write would match no row; drop it.");
            }

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
