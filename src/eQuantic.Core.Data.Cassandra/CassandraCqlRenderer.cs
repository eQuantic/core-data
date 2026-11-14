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
    /// <summary>
    ///     How many keys one statement's <c>IN</c> binds when the provider can split it: a longer list runs as one statement
    ///     per chunk. One <c>IN</c> over many partitions makes one coordinator fetch them all, and a statement binds at most
    ///     <see cref="CassandraStatements.MaxValues" /> values: a key list the provider read (a relation's keys, an include's
    ///     parents) has no bound of its own.
    /// </summary>
    public const int InChunkSize = 100;

    /// <summary>
    ///     Whether the filter is an <c>IN</c> this renderer pushes down (on a primary-key column, no <c>null</c> among its
    ///     values) that holds more keys than one chunk.
    /// </summary>
    public static bool IsChunkable(CassandraEntityConfiguration configuration, QueryFilter filter) =>
        filter is InFilter { Values.Count: > InChunkSize } inFilter
        && inFilter.Values.All(value => value is not null)
        && configuration.Columns.Any(column => CassandraEntityConfiguration.Same(column.Member, inFilter.Member))
        && configuration.IsKey(configuration.ColumnFor(inFilter.Member));

    /// <summary>The <c>IN</c> rendered in chunks of <see cref="InChunkSize" /> keys, each clause with its stored values.</summary>
    public static IReadOnlyList<(string Cql, object?[] Values)> InChunks(CassandraEntityConfiguration configuration, InFilter filter)
    {
        var column = Column(configuration, filter.Member);
        // Distinct keys: a row is then in one chunk only, so what the chunks count or sum adds up.
        return filter.Values.Distinct().Chunk(InChunkSize)
            .Select(chunk => ($"{column} IN ({string.Join(", ", chunk.Select(_ => "?"))})",
                chunk.Select(value => Stored(configuration, column, value)).ToArray()))
            .ToList();
    }

    public static (string Cql, object?[] Values, bool RequiresAllowFiltering) Render(CassandraEntityConfiguration configuration, QueryFilter filter)
    {
        var clauses = new List<string>();
        var values = new List<object?>();
        var requiresFiltering = false;

        Conjunction(filter, configuration, clauses, values, ref requiresFiltering);
        return (string.Join(" AND ", clauses), values.ToArray(), requiresFiltering);
    }

    /// <summary>
    ///     Attempts to render the filter, reporting failure instead of throwing. The strict <see cref="Render" />
    ///     stays the single source of truth on what CQL can express — the pushdown engine turns its refusals into
    ///     client-side residual work.
    /// </summary>
    public static bool TryRender(CassandraEntityConfiguration configuration, QueryFilter filter,
        out (string Cql, object?[] Values, bool RequiresAllowFiltering) rendered)
    {
        try
        {
            rendered = Render(configuration, filter);
            return true;
        }
        catch (NotSupportedException)
        {
            rendered = default;
            return false;
        }
    }

    /// <summary>Whether the filter pins the partition (an equality/IN on a partition key column, AND-reachable).</summary>
    public static bool PinsPartition(CassandraEntityConfiguration configuration, QueryFilter filter) => filter switch
    {
        ComparisonFilter { Operator: ComparisonOperator.Equal } comparison =>
            configuration.PartitionKeys.Any(key => CassandraEntityConfiguration.Same(key, configuration.ColumnFor(comparison.Member))),
        InFilter inFilter =>
            configuration.PartitionKeys.Any(key => CassandraEntityConfiguration.Same(key, configuration.ColumnFor(inFilter.Member))),
        LogicalFilter { Operator: LogicalOperator.And } and =>
            and.Operands.Any(operand => PinsPartition(configuration, operand)),
        _ => false,
    };

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
                clauses.Add(Comparison(comparison, Column(configuration, comparison.Member), configuration, values, ref requiresFiltering));
                return;
            case InFilter inFilter:
                clauses.Add(In(inFilter, Column(configuration, inFilter.Member), configuration, values, ref requiresFiltering));
                return;
            case CollectionFilter collection:
                clauses.Add(Collection(collection, Column(configuration, collection.Member), values, ref requiresFiltering));
                return;
            case TupleComparisonFilter tuple:
                clauses.Add(Tuple(tuple, tuple.Members.Select(member => Column(configuration, member)).ToList(),
                    configuration, values, ref requiresFiltering));
                return;
            case StringFilter text:
                clauses.Add(Like(configuration, text.Member, Column(configuration, text.Member), text.Operator switch
                {
                    StringOperator.StartsWith => EscapedFragment(text.Value) + "%",
                    StringOperator.EndsWith => "%" + EscapedFragment(text.Value),
                    _ => "%" + EscapedFragment(text.Value) + "%",
                }, prefixOnly: text.Operator == StringOperator.StartsWith, values));
                return;
            case TextFilter text:
                clauses.Add(IgnoringCase(configuration, text, values));
                return;
            case FunctionFilter { Function: "Like", Operator: null, Arguments: [string pattern] } like:
                clauses.Add(Like(configuration, like.Member, Column(configuration, like.Member), pattern,
                    prefixOnly: !pattern.StartsWith('%') && pattern.EndsWith('%') && pattern.IndexOf('%') == pattern.Length - 1,
                    values));
                return;
            default:
                throw new NotSupportedException($"Cannot render the filter '{filter.GetType().Name}' to CQL.");
        }
    }

    /// <summary>
    ///     Resolves a CLR member to its stored column name. A member no column stores (a nested path, a computed
    ///     pseudo-member) cannot render — it refuses into residual.
    /// </summary>
    private static string Column(CassandraEntityConfiguration configuration, string member) =>
        configuration.Columns.FirstOrDefault(column => CassandraEntityConfiguration.Same(column.Member, member))?.Name
        ?? throw new NotSupportedException($"'{member}' is not a mapped column; the clause runs client-side.");

    /// <summary>
    ///     Renders a <c>LIKE</c> — only on a column the model declared a search index for, and only when the
    ///     index's mode can serve the pattern; every refusal degrades to the gated client-side residual. The
    ///     index serves the match, so <c>ALLOW FILTERING</c> is not required.
    /// </summary>
    private static string Like(CassandraEntityConfiguration configuration, string member, string column, string pattern,
        bool prefixOnly, List<object?> values)
    {
        if (!configuration.CanLike(column, out var mode))
        {
            throw new NotSupportedException(
                $"'{member}' has no search index; declare one with SearchIndex(x => x.{member}) to push LIKE down, " +
                "or run the match client-side.");
        }

        if (mode == CassandraSearchMode.Prefix && !prefixOnly)
        {
            throw new NotSupportedException(
                $"The search index on '{member}' matches prefixes only; declare it with CassandraSearchMode.Contains " +
                "for substring matches, or run the match client-side.");
        }

        values.Add(pattern);
        return $"{column} LIKE ?";
    }

    /// <summary>
    ///     Renders a text test that ignores case as a <c>LIKE</c> — only on a column whose search index matches without
    ///     regard to case (a non-tokenizing, case-insensitive SASI analyzer), and only when the index's mode serves the
    ///     match. Every refusal degrades to the gated client-side residual, where C# decides — a test over the lower-cased
    ///     text whose value holds an upper-case letter included.
    /// </summary>
    private static string IgnoringCase(CassandraEntityConfiguration configuration, TextFilter text, List<object?> values)
    {
        var column = Column(configuration, text.Member);
        if (!configuration.CanLikeIgnoringCase(column, out var mode))
        {
            throw new NotSupportedException(
                $"'{text.Member}' has no case-insensitive search index; declare one with SearchIndex(x => x.{text.Member}, mode, " +
                "caseInsensitive: true) to push the match down, or run it client-side.");
        }

        if (!text.CanMatch)
        {
            throw new NotSupportedException("A test over the folded text whose value holds a letter of the other case matches nothing; it runs client-side.");
        }

        if (text.Value.Contains('%'))
        {
            throw new NotSupportedException("The value contains a literal '%' and SASI's LIKE has no escape syntax; run the match client-side.");
        }

        if (mode == CassandraSearchMode.Prefix && text.Match is not (TextMatch.StartsWith or TextMatch.Equals))
        {
            throw new NotSupportedException(
                $"The search index on '{text.Member}' matches prefixes only; declare it with CassandraSearchMode.Contains, or run the match client-side.");
        }

        values.Add(text.Match switch
        {
            TextMatch.Equals => text.Value,
            TextMatch.StartsWith => text.Value + "%",
            TextMatch.EndsWith => "%" + text.Value,
            _ => "%" + text.Value + "%",
        });
        return $"{column} LIKE ?";
    }

    /// <summary>SASI's <c>LIKE</c> has no escape clause — a literal wildcard in the value cannot push down.</summary>
    private static string EscapedFragment(string value) =>
        value.IndexOfAny(['%', '_']) < 0
            ? value
            : throw new NotSupportedException(
                "The value contains a literal '%' or '_' and Cassandra LIKE has no escape syntax; run the match client-side.");

    private static string Comparison(ComparisonFilter filter, string column, CassandraEntityConfiguration configuration, List<object?> values, ref bool requiresFiltering)
    {
        var isPartition = configuration.PartitionKeys.Any(key => CassandraEntityConfiguration.Same(key, column));

        if (filter.Value is null)
        {
            throw new NotSupportedException(
                $"Cassandra CQL cannot compare '{filter.Member}' to NULL (an unset column is simply absent from the row); filter on a concrete value.");
        }

        if (filter.Operator is ComparisonOperator.NotEqual)
        {
            throw new NotSupportedException($"Cassandra CQL has no '<>' operator; '{filter.Member} <> ?' is not expressible.");
        }

        // An enum stored as its labels orders by its members' values, as C# does, while CQL would compare the labels' text:
        // the comparison is membership in the labels whose member satisfies it, which CQL takes on the primary key only.
        // Elsewhere, or when no member satisfies it, the clause runs client-side, where C# decides.
        if (EnumOrder.IsOrdered(filter.Operator) && configuration.Column(column) is { CqlType: "text" } stored
                                                && (Nullable.GetUnderlyingType(stored.ClrType) ?? stored.ClrType) is { IsEnum: true } labelEnum)
        {
            var satisfying = EnumOrder.Satisfying(labelEnum, filter.Operator, filter.Value);
            if (satisfying.Count == 0)
            {
                throw new NotSupportedException($"No member of '{labelEnum.Name}' satisfies the comparison of '{filter.Member}'; it runs client-side.");
            }

            return In(new InFilter(filter.Member, satisfying.Cast<object?>().ToList()), column, configuration, values, ref requiresFiltering);
        }

        if (filter.Operator is ComparisonOperator.Equal)
        {
            if (!isPartition && !configuration.IsClusteringKey(column))
            {
                requiresFiltering = true;
            }

            values.Add(Stored(configuration, column, filter.Value));
            return $"{column} = ?";
        }

        // A range on the partition key is expressed through token(); ranges on clustering keys are native.
        if (isPartition)
        {
            values.Add(Stored(configuration, column, filter.Value));
            return $"token({column}) {Operator(filter.Operator)} token(?)";
        }

        if (!configuration.IsClusteringKey(column))
        {
            requiresFiltering = true;
        }

        values.Add(Stored(configuration, column, filter.Value));
        return $"{column} {Operator(filter.Operator)} ?";
    }

    private static string In(InFilter filter, string column, CassandraEntityConfiguration configuration, List<object?> values, ref bool requiresFiltering)
    {
        if (filter.Values.Any(value => value is null))
        {
            throw new NotSupportedException(
                $"Cassandra CQL cannot match '{filter.Member}' against a set containing NULL (an unset column is simply absent from the row).");
        }

        if (!configuration.IsKey(column))
        {
            // Cassandra takes IN only on the primary key ("IN predicates on non-primary-key columns are not yet
            // supported"), with or without ALLOW FILTERING: the clause runs client-side.
            throw new NotSupportedException(
                $"Cassandra CQL takes IN only on the primary key; '{filter.Member} IN (…)' runs client-side.");
        }

        values.AddRange(filter.Values.Select(value => Stored(configuration, column, value)));
        return $"{column} IN ({string.Join(", ", filter.Values.Select(_ => "?"))})";
    }

    private static string Collection(CollectionFilter filter, string column, List<object?> values, ref bool requiresFiltering)
    {
        if (filter.Value is null)
        {
            throw new NotSupportedException(
                $"Cassandra CQL cannot test '{filter.Member}' CONTAINS NULL; collections never hold NULL elements.");
        }

        // CONTAINS / CONTAINS KEY need a secondary index or ALLOW FILTERING.
        requiresFiltering = true;
        values.Add(filter.Value);
        return filter.Key ? $"{column} CONTAINS KEY ?" : $"{column} CONTAINS ?";
    }

    private static string Tuple(TupleComparisonFilter filter, IReadOnlyList<string> columns, CassandraEntityConfiguration configuration, List<object?> values, ref bool requiresFiltering)
    {
        if (!columns.All(configuration.IsClusteringKey))
        {
            requiresFiltering = true;
        }

        values.AddRange(filter.Values.Select((value, index) => Stored(configuration, columns[index], value)));
        return $"({string.Join(", ", columns)}) {Operator(filter.Operator)} ({string.Join(", ", filter.Values.Select(_ => "?"))})";
    }

    /// <summary>A filter value in the form its column stores (an enum's label or number, a JSON member's text).</summary>
    private static object? Stored(CassandraEntityConfiguration configuration, string column, object? value) =>
        CassandraValues.ToStored(configuration.Column(column), value);

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
