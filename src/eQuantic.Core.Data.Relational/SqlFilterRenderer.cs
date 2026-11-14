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
    /// <param name="dialect">The dialect.</param>
    /// <param name="configuration">The mapping of the entity the filter reads.</param>
    /// <param name="filter">The filter.</param>
    /// <param name="parameters">The statement's bound values, appended to.</param>
    /// <param name="relations">
    ///     The model's relations, when the filter may cross one (a <see cref="RelationPredicates.RelatedRowExists" />
    ///     marker); without them such a filter is refused.
    /// </param>
    /// <param name="qualifier">
    ///     The table reference the filter's columns are qualified with — a subquery's alias — or <c>null</c> to leave them
    ///     unqualified (the statement's own table, which a correlation then names by its table name).
    /// </param>
    public static string Render(SqlDialect dialect, RelationalEntityConfiguration configuration, QueryFilter filter,
        List<object?> parameters, RelationalRelations? relations = null, string? qualifier = null)
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
            StringFilter text => Like(text),
            TextFilter text => Text(text),
            FunctionFilter { Function: RelationPredicates.RelatedRowExistsFunction, Arguments: [RelationPredicate relation] } exists =>
                Exists(exists.Member, relation),
            FunctionFilter function => Function(function),
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

            // An enum stored as its labels orders by its members' values, as C# does: the comparison is membership in the
            // labels whose member satisfies it, never the labels' text (or PostgreSQL's declaration order).
            if (EnumOrder.IsOrdered(comparison.Operator) && LabelEnum(comparison.Member) is { } labelEnum)
            {
                return In(new InFilter(comparison.Member, EnumOrder.Satisfying(labelEnum, comparison.Operator, comparison.Value)));
            }

            // C# != matches NULL rows too; SQL <> filters them out — keep the C# semantics explicit.
            return comparison.Operator == ComparisonOperator.NotEqual
                ? $"({column} <> {Bind(Stored(comparison.Member, comparison.Value))} OR {column} IS NULL)"
                : $"{column} {Operator(comparison.Operator)} {Bind(Stored(comparison.Member, comparison.Value))}";
        }

        string Like(StringFilter text)
        {
            var pattern = text.Operator switch
            {
                StringOperator.StartsWith => Escaped(text.Value) + "%",
                StringOperator.EndsWith => "%" + Escaped(text.Value),
                _ => "%" + Escaped(text.Value) + "%",
            };
            return $"{Column(text.Member)} LIKE {Bind(pattern)} ESCAPE '!'";
        }

        // Wildcards in the tested value are literals to C#; escape them. '!' parses identically in every dialect
        // (backslash would need per-dialect literal rules), and each dialect names its own wildcards.
        string Escaped(string value)
        {
            var escaped = value.Replace("!", "!!");
            foreach (var wildcard in dialect.LikeWildcards)
            {
                escaped = escaped.Replace(wildcard.ToString(), "!" + wildcard);
            }

            return escaped;
        }

        // Without regard to case: equality with both sides lower-cased by the store (one folding for both), a part of
        // the text through the dialect's case-insensitive LIKE (PostgreSQL's ILIKE). Against the member's lower- or
        // upper-cased text: the value as it is, as C# compares it.
        string Text(TextFilter text)
        {
            if (!text.CanMatch)
            {
                // A lower-cased text holds no upper-case letter: C# finds nothing, whatever the collation would say.
                return dialect.FalseLiteral;
            }

            var fold = text.Case == TextCase.Upper ? "UPPER" : "LOWER";
            var value = text.Match switch
            {
                TextMatch.Equals => text.Value,
                TextMatch.StartsWith => Escaped(text.Value) + "%",
                TextMatch.EndsWith => "%" + Escaped(text.Value),
                _ => "%" + Escaped(text.Value) + "%",
            };

            if (text.Case == TextCase.Ignore)
            {
                return text.Match == TextMatch.Equals
                    ? $"LOWER({Column(text.Member)}) = LOWER({Bind(value)})"
                    : dialect.LikeIgnoringCase(Column(text.Member), Bind(value));
            }

            var folded = $"{fold}({Column(text.Member)})";
            return text.Match == TextMatch.Equals ? $"{folded} = {Bind(value)}" : $"{folded} LIKE {Bind(value)} ESCAPE '!'";
        }

        // A filter across a relation: a correlated EXISTS over the related table, its rows matched to this one by the
        // navigation's keys, and holding the relation's condition and the related entity's global filter.
        string Exists(string ownerKey, RelationPredicate relation)
        {
            if (relations is null)
            {
                throw new NotSupportedException($"'{relation.Text}' crosses a relation, which this statement cannot express.");
            }

            var navigation = relation.Navigation;
            var target = relations.Model.For(navigation.Target);
            var alias = dialect.Quote(relations.NextAlias());
            var targetKey = target.ColumnFor(navigation.TargetKey.Name)
                            ?? throw new NotSupportedException($"'{navigation.Target.Name}' has no mapped member '{navigation.TargetKey.Name}'.");
            var owner = qualifier ?? dialect.Quote(configuration.TableName);
            var where = $"{alias}.{dialect.Quote(targetKey.Name)} = {owner}.{dialect.Quote(ColumnOf(ownerKey).Name)}";

            var condition = RelationalRelations.And(relation.Condition, relations.Scope(navigation.Target));
            if (condition is not null)
            {
                var marked = RelationPredicates.Mark(PredicateNormalizer.Normalize(condition), relations.Navigation);
                where += " AND " + Render(dialect, target, FilterInterpreter.Interpret(marked), parameters, relations, alias);
            }

            return $"EXISTS (SELECT 1 FROM {dialect.Quote(target.TableName)} {alias} WHERE {where})";
        }

        string Function(FunctionFilter function)
        {
            var column = Column(function.Member);
            var markers = function.Arguments.Select(Bind).ToList();
            if (!dialect.Functions.TryRender(function.Function, column, markers, out var fragment))
            {
                throw new NotSupportedException(
                    $"No SQL translation is registered for '{function.Function}'; map one with Functions.Map(\"{function.Function}\", ...) or run it client-side.");
            }

            if (function.Operator is not { } op)
            {
                return fragment;
            }

            return function.Value is null
                ? throw new NotSupportedException($"A comparison of '{function.Function}({function.Member})' to NULL has no defined result.")
                : $"{fragment} {Operator(op)} {Bind(function.Value)}";
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

            var list = $"{column} IN ({string.Join(", ", values.Select(value => Bind(Stored(inFilter.Member, value))))})";
            return hasNull ? $"({list} OR {column} IS NULL)" : list;
        }

        string Column(string member) =>
            (qualifier is null ? string.Empty : qualifier + ".") + dialect.Quote(ColumnOf(member).Name);

        RelationalColumn ColumnOf(string member) =>
            configuration.ColumnFor(member)
            ?? throw new NotSupportedException($"'{configuration.EntityType.Name}' has no mapped member '{member}'.");

        // The enum a member is stored as the labels of, or null.
        Type? LabelEnum(string member) =>
            configuration.ColumnFor(member) is { Converter: null } column && dialect.NativeEnumOf(column.StoredType) is { } native
                ? native.EnumType
                : null;

        // A converted member's filter values bind as their stored form — the domain type never reaches the driver.
        object? Stored(string member, object? value)
        {
            if (configuration.ColumnFor(member) is not { } column)
            {
                return value;
            }

            if (column.Converter is not null)
            {
                return column.Store(value);
            }

            // C# compiles a comparison with an enum member into one of numbers ((int)x.State == 1), so the value
            // arrives as the number. A native enum column compares only with its own type: the value goes back
            // to being the enum, which the driver sends as its label.
            var type = Nullable.GetUnderlyingType(column.StoredType) ?? column.StoredType;
            return value is not null and not Enum && type.IsEnum
                                                   && dialect.NativeEnums.Any(native => native.EnumType == type)
                ? Enum.ToObject(type, value)
                : value;
        }

        string Bind(object? value)
        {
            parameters.Add(dialect.BindValue(value));
            return "@p" + (parameters.Count - 1);
        }
    }

    /// <summary>
    ///     Renders the filter with its values written in as the dialect's literals (<see cref="SqlDialect.Literal" />), for a
    ///     statement that has to carry the predicate itself: a filtered index's DDL, which takes no parameters, and an
    ///     upsert's conflict filter, which names that index's predicate.
    /// </summary>
    /// <param name="dialect">The dialect.</param>
    /// <param name="configuration">The mapping of the entity the filter reads.</param>
    /// <param name="filter">The filter.</param>
    public static string RenderInlined(SqlDialect dialect, RelationalEntityConfiguration configuration, QueryFilter filter)
    {
        var values = new List<object?>();
        var fragment = Render(dialect, configuration, filter, values);

        // One pass over the markers: a literal whose text holds a marker is never read as one.
        return System.Text.RegularExpressions.Regex.Replace(fragment, @"@p(\d+)\b", marker =>
            dialect.Literal(values[int.Parse(marker.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)]));
    }

    /// <summary>Attempts to render, reporting failure instead of throwing — the engine turns refusals into residual work.</summary>
    public static bool TryRender(SqlDialect dialect, RelationalEntityConfiguration configuration, QueryFilter filter,
        List<object?> parameters, out string sql, RelationalRelations? relations = null)
    {
        var checkpoint = parameters.Count;
        try
        {
            sql = Render(dialect, configuration, filter, parameters, relations);
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
