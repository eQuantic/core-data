using System.Collections;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;
using System.Reflection;
using eQuantic.Core.Data.Cassandra.Repository;
using eQuantic.Core.Data.Query;
using eQuantic.Core.Data.Repository.Options;

namespace eQuantic.Core.Data.Cassandra;

/// <summary>
///     Loads related rows from their own tables. Cassandra rows are self-contained and CQL has no join, so each
///     <c>Include</c> segment is <b>one second query</b> over the related table — the parents' keys resolved and read
///     with <c>IN</c> — under the same gates as any read and the read's own opt-ins (<c>.AllowFiltering()</c> when the
///     foreign key is not the related table's partition key, <c>.AllowClientEvaluation()</c> for a filter CQL cannot
///     express). A shape's order, skip and limit apply per parent as the rows are stitched; when the foreign key is the
///     partition key and the order is the clustering order, the limit pushes down as <c>PER PARTITION LIMIT</c>.
///     A relation count is <c>SELECT fk, COUNT(*) … GROUP BY fk</c> when the foreign key is the partition key and the
///     filter pushes down; otherwise the foreign keys (and the columns a client-side filter reads) are fetched and
///     counted client-side, behind <c>.AllowClientEvaluation()</c>.
/// </summary>
internal static class CassandraInclude
{
    private const BindingFlags Members = BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance;

    /// <summary>Loads every include path of the options into the entities, level by level.</summary>
    public static async Task ApplyAsync<TEntity>(CassandraUnitOfWork unitOfWork, List<TEntity> entities, QueryOptions<TEntity> options)
        where TEntity : class
    {
        if (entities.Count == 0)
        {
            return;
        }

        foreach (var path in options.IncludePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            IReadOnlyList<object> current = entities;
            var currentType = typeof(TEntity);
            var depth = 0;
            foreach (var segment in path.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var shape = depth++ == 0 && options.IncludeShapes.TryGetValue(segment, out var declared) ? declared : null;
                var navigation = currentType.GetProperty(segment, Members)
                                 ?? throw new NotSupportedException(
                                     $"Cannot include '{segment}': '{currentType.Name}' has no such navigation property.");

                var elementType = RelationCountInterpreter.ElementType(navigation.PropertyType);
                if (unitOfWork.ConfigurationOf(elementType ?? navigation.PropertyType) is null)
                {
                    throw new NotSupportedException(
                        $"Cannot include '{segment}': it is not a navigation to an entity of the Cassandra model " +
                        $"('{(elementType ?? navigation.PropertyType).Name}' is not registered) — a row's own columns load with it.");
                }

                if (elementType is null && shape is not null)
                {
                    throw new NotSupportedException(
                        $"Cannot shape the include '{segment}': a filter, an order or a limit applies to a collection, and it is a reference.");
                }

                var method = elementType is null ? nameof(ReferenceAsync) : nameof(CollectionAsync);
                var target = elementType ?? navigation.PropertyType;
                current = await ((Task<IReadOnlyList<object>>)typeof(CassandraInclude)
                    .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(currentType, target)
                    .Invoke(null, [unitOfWork, current, navigation, shape, options])!).ConfigureAwait(false);
                currentType = target;

                if (current.Count == 0)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Counts, for each entity, the related rows of each count; one array of counts per entity, in order.</summary>
    public static async Task<long[][]> CountAsync<TEntity>(CassandraUnitOfWork unitOfWork, IReadOnlyList<TEntity> entities,
        IReadOnlyList<RelationCount> counts, object? options) where TEntity : class
    {
        var result = entities.Select(_ => new long[counts.Count]).ToArray();
        if (entities.Count == 0)
        {
            return result;
        }

        var key = Key(unitOfWork, typeof(TEntity));
        var keys = entities.Select(key.GetValue).Where(value => value is not null).Distinct().ToList();
        for (var index = 0; index < counts.Count; index++)
        {
            var count = counts[index];
            var perKey = await ((Task<Dictionary<string, long>>)typeof(CassandraInclude)
                .GetMethod(nameof(GroupCountAsync), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(typeof(TEntity), count.ElementType)
                .Invoke(null, [unitOfWork, keys, count, options])!).ConfigureAwait(false);

            for (var row = 0; row < entities.Count; row++)
            {
                result[row][index] = key.GetValue(entities[row]) is { } parent && perKey.TryGetValue(KeyText(parent), out var found) ? found : 0;
            }
        }

        return result;
    }

    /// <summary>Whether a member is a collection of rows of a registered entity holding a foreign key back to the parent.</summary>
    public static bool IsRelation(CassandraUnitOfWork unitOfWork, Type parentType, PropertyInfo member) =>
        RelationCountInterpreter.ElementType(member.PropertyType) is { IsClass: true } element
        && element != typeof(string)
        && unitOfWork.ConfigurationOf(element) is not null
        && ForeignKey(element, parentType.Name) is not null;

    // ---------------------------------------------------------------- typed cores

    private static async Task<IReadOnlyList<object>> CollectionAsync<TParent, TElement>(CassandraUnitOfWork unitOfWork,
        IReadOnlyList<object> parents, PropertyInfo navigation, IncludeShape? shape, object? options)
        where TElement : class
    {
        var key = Key(unitOfWork, typeof(TParent));
        var foreignKey = ForeignKey(typeof(TElement), typeof(TParent).Name)
                         ?? throw new NotSupportedException(
                             $"Cannot include '{navigation.Name}': no foreign key found on '{typeof(TElement).Name}' back to " +
                             $"'{typeof(TParent).Name}' (expected [ForeignKey(\"{typeof(TParent).Name}\")] or a '{typeof(TParent).Name}Id' property).");

        var keys = parents.Select(key.GetValue).Where(value => value is not null).Distinct().ToList();
        var configuration = unitOfWork.ConfigurationOf(typeof(TElement))
                            ?? throw new InvalidOperationException($"'{typeof(TElement).Name}' is not registered in the Cassandra model.");
        var elements = await ReadAsync<TElement>(unitOfWork, configuration, In<TElement>(foreignKey, keys),
            shape?.Filter as Expression<Func<TElement, bool>>, options, $"the include of '{navigation.Name}'",
            PerPartitionLimit(configuration, foreignKey, shape)).ConfigureAwait(false);

        var byParent = elements.GroupBy(element => KeyText(foreignKey.GetValue(element)))
            .ToDictionary(group => group.Key, group => (IEnumerable<TElement>)group);

        var loaded = new List<object>();
        foreach (var parent in parents)
        {
            IEnumerable<TElement> related = key.GetValue(parent) is { } value && byParent.TryGetValue(KeyText(value), out var found)
                ? found
                : [];
            var kept = shape is null ? related.ToList() : ReadShapes.Page(related, shape);
            navigation.SetValue(parent, navigation.PropertyType.IsArray ? kept.ToArray() : kept);
            loaded.AddRange(kept);
        }

        return loaded;
    }

    private static async Task<IReadOnlyList<object>> ReferenceAsync<TParent, TTarget>(CassandraUnitOfWork unitOfWork,
        IReadOnlyList<object> parents, PropertyInfo navigation, IncludeShape? shape, object? options)
        where TTarget : class
    {
        _ = shape;
        var localKey = ForeignKey(typeof(TParent), navigation.Name)
                       ?? throw new NotSupportedException(
                           $"Cannot include '{navigation.Name}': no '{navigation.Name}Id' property found on '{typeof(TParent).Name}'.");
        var configuration = unitOfWork.ConfigurationOf(typeof(TTarget))
                            ?? throw new InvalidOperationException($"'{typeof(TTarget).Name}' is not registered in the Cassandra model.");
        var targetKey = Key(unitOfWork, typeof(TTarget));
        var ids = parents.Select(localKey.GetValue).Where(value => value is not null).Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var targets = await ReadAsync<TTarget>(unitOfWork, configuration, In<TTarget>(targetKey, ids), null, options,
            $"the include of '{navigation.Name}'", null).ConfigureAwait(false);
        var byKey = targets.GroupBy(target => KeyText(targetKey.GetValue(target))).ToDictionary(group => group.Key, group => group.First());
        foreach (var parent in parents)
        {
            if (localKey.GetValue(parent) is { } id && byKey.TryGetValue(KeyText(id), out var match))
            {
                navigation.SetValue(parent, match);
            }
        }

        return targets;
    }

    private static async Task<Dictionary<string, long>> GroupCountAsync<TParent, TElement>(CassandraUnitOfWork unitOfWork,
        List<object?> keys, RelationCount count, object? parentOptions) where TElement : class
    {
        var counts = new Dictionary<string, long>();
        if (keys.Count == 0)
        {
            return counts;
        }

        var configuration = unitOfWork.ConfigurationOf(typeof(TElement))
                            ?? throw new InvalidOperationException($"'{typeof(TElement).Name}' is not registered in the Cassandra model.");
        var foreignKey = ForeignKey(typeof(TElement), typeof(TParent).Name)
                         ?? throw new NotSupportedException(
                             $"Cannot count '{count.Navigation.Name}': no foreign key found on '{typeof(TElement).Name}' back to '{typeof(TParent).Name}'.");
        var column = configuration.ColumnFor(foreignKey.Name);
        var options = Options<TElement>(parentOptions, count.Predicate as Expression<Func<TElement, bool>>);
        var plan = CassandraCql.Plan(configuration, options, In<TElement>(foreignKey, keys));
        var consistency = CassandraQueryOptionsExtensions.ConsistencyOf(parentOptions);

        if (plan.Residual.Count == 0 && plan.Alternatives.Count == 0
            && configuration.PartitionKeys is [var partition] && CassandraEntityConfiguration.Same(partition, column))
        {
            // The foreign key is the partition key: the cluster groups and counts, only the counts travel.
            var cql = $"SELECT {column}, COUNT(*) FROM {configuration.TableName} WHERE {plan.Where} GROUP BY {column}"
                      + (plan.RequiresAllowFiltering ? " ALLOW FILTERING" : string.Empty);
            Gate(plan, options, $"the count of '{count.Navigation.Name}'", clientWork: false);
            var rows = await CassandraStatements.ExecuteAsync(unitOfWork.GetSession(), cql, plan.Values, consistency,
                unitOfWork.CommandLogger, unitOfWork.SensitiveLogging).ConfigureAwait(false);
            foreach (var row in rows)
            {
                counts[KeyText(row.GetValue<object>(0))] = row.GetValue<long>(1);
            }

            return counts;
        }

        // Counted client-side: only the foreign key and the columns a client-side filter reads travel.
        Gate(plan, options, $"the count of '{count.Navigation.Name}'", clientWork: true);
        var elements = await ReadAsync<TElement>(unitOfWork, configuration, In<TElement>(foreignKey, keys),
            options.Filter, parentOptions, $"the count of '{count.Navigation.Name}'", null,
            [column, .. ResidualColumns(configuration, plan)]).ConfigureAwait(false);
        foreach (var group in elements.GroupBy(element => KeyText(foreignKey.GetValue(element))))
        {
            counts[group.Key] = group.LongCount();
        }

        return counts;
    }

    /// <summary>
    ///     Reads related rows: <c>IN</c> over the keys, the filter pushed where CQL can express it and evaluated over the
    ///     fetched rows where it cannot — through the same planner and gates as any read, under the read's opt-ins.
    /// </summary>
    private static async Task<List<TElement>> ReadAsync<TElement>(CassandraUnitOfWork unitOfWork,
        CassandraEntityConfiguration configuration, Expression<Func<TElement, bool>> keys, Expression<Func<TElement, bool>>? filter,
        object? parentOptions, string what, int? perPartitionLimit, IReadOnlyCollection<string>? columns = null)
        where TElement : class
    {
        var options = Options(parentOptions, filter);
        var plan = CassandraCql.Plan(configuration, options, keys);
        Gate(plan, options, what, clientWork: false);

        var selected = columns is null ? null : new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
        var push = perPartitionLimit is not null && plan.Residual.Count == 0;
        var cql = $"SELECT {(selected is null ? "*" : string.Join(", ", selected))} FROM {configuration.TableName} WHERE {plan.Where}"
                  + (push ? $" PER PARTITION LIMIT {perPartitionLimit}" : string.Empty)
                  + (plan.RequiresAllowFiltering ? " ALLOW FILTERING" : string.Empty);
        var rows = await CassandraStatements.ExecuteAsync(unitOfWork.GetSession(), cql, plan.Values,
            CassandraQueryOptionsExtensions.ConsistencyOf(parentOptions), unitOfWork.CommandLogger, unitOfWork.SensitiveLogging)
            .ConfigureAwait(false);

        var residuals = plan.Residual.Select(predicate => ((Expression<Func<TElement, bool>>)predicate).Compile()).ToList();
        return rows.Select(row => CassandraMapper.Materialize<TElement>(configuration, row, selected))
            .Where(element => residuals.All(residual => residual(element)))
            .ToList();
    }

    /// <summary>The read's gates, applied to a related read: a scan needs <c>.AllowFiltering()</c>, client work <c>.AllowClientEvaluation()</c>.</summary>
    private static void Gate<TElement>(CassandraCqlPlan plan, QueryOptions<TElement> options, string what, bool clientWork)
        where TElement : class
    {
        if (plan.Alternatives.Count > 0)
        {
            throw new NotSupportedException(
                $"{Capitalized(what)} cannot split an OR into several queries; restructure its filter around the keys.");
        }

        if (plan.RequiresAllowFiltering && !CassandraCql.AllowFilteringOptedIn(options))
        {
            throw new NotSupportedException(
                $"{Capitalized(what)} filters on columns outside the related table's primary key (its foreign key is not the partition " +
                "key); call .AllowFiltering() on the read to opt into the scan, or partition the related table by the foreign key.");
        }

        if ((plan.Residual.Count > 0 || clientWork) && !CassandraCql.ClientEvaluationOptedIn(options))
        {
            throw new NotSupportedException(
                $"{Capitalized(what)} runs client-side over the fetched rows" +
                (plan.Residual.Count > 0 ? $" ('{plan.ResidualText}' cannot be expressed in CQL)" : " (CQL groups by the partition key only)") +
                "; call .AllowClientEvaluation() on the read to opt in, or partition the related table by the foreign key.");
        }
    }

    private static string Capitalized(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>The related read's options: the filter, and the opt-ins and consistency the parent's read carries.</summary>
    private static QueryOptions<TElement> Options<TElement>(object? parentOptions, Expression<Func<TElement, bool>>? filter)
        where TElement : class
    {
        var options = new QueryOptions<TElement>();
        if (filter is not null)
        {
            options.Where(filter);
        }

        if (CassandraQueryOptionsExtensions.IsAllowFilteringOptedIn(parentOptions))
        {
            options.AllowFiltering();
        }

        if (CassandraQueryOptionsExtensions.IsClientEvaluationOptedIn(parentOptions))
        {
            options.AllowClientEvaluation();
        }

        if (CassandraQueryOptionsExtensions.ConsistencyOf(parentOptions) is { } consistency)
        {
            options.WithConsistency(consistency);
        }

        return options;
    }

    /// <summary>
    ///     The limit a shape pushes down as <c>PER PARTITION LIMIT</c>: when the foreign key is the related table's
    ///     whole partition key and the shape orders by the clustering columns as they are stored (or not at all), the
    ///     first rows of each partition are the ones each parent keeps.
    /// </summary>
    private static int? PerPartitionLimit(CassandraEntityConfiguration configuration, PropertyInfo foreignKey, IncludeShape? shape)
    {
        if (shape?.TakeCount is not { } take
            || configuration.PartitionKeys is not [var partition]
            || !CassandraEntityConfiguration.Same(partition, configuration.ColumnFor(foreignKey.Name))
            || shape.Sortings.Count > configuration.ClusteringKeys.Count)
        {
            return null;
        }

        for (var index = 0; index < shape.Sortings.Count; index++)
        {
            var sort = shape.Sortings[index];
            var clustering = configuration.ClusteringKeys[index];
            if (MemberOf(sort.KeySelector) is not { } member
                || !CassandraEntityConfiguration.Same(configuration.ColumnFor(member), clustering.Column)
                || sort.Descending != clustering.Descending)
            {
                return null;
            }
        }

        return (shape.SkipCount ?? 0) + take;
    }

    private static string? MemberOf(LambdaExpression selector)
    {
        var body = selector.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            body = unary.Operand;
        }

        return body is MemberExpression { Expression: ParameterExpression } member ? member.Member.Name : null;
    }

    private static IEnumerable<string> ResidualColumns(CassandraEntityConfiguration configuration, CassandraCqlPlan plan) =>
        plan.Residual.SelectMany(predicate => new MemberCollector(predicate.Parameters[0]).Collect(predicate.Body))
            .Select(configuration.ColumnFor);

    /// <summary>Builds <c>x =&gt; keys.Contains(x.Member)</c>: an <c>IN</c> over the keys.</summary>
    private static Expression<Func<T, bool>> In<T>(PropertyInfo member, IReadOnlyList<object?> values)
    {
        var type = member.PropertyType;
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!;
        foreach (var value in values)
        {
            list.Add(value is null || type.IsInstanceOfType(value) ? value : Convert.ChangeType(value, Nullable.GetUnderlyingType(type) ?? type));
        }

        var parameter = Expression.Parameter(typeof(T), "x");
        var contains = Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [type],
            Expression.Constant(list), Expression.Property(parameter, member));
        return Expression.Lambda<Func<T, bool>>(contains, parameter);
    }

    /// <summary>A key compared by its text, so a foreign key and the key it names match whatever their CLR types.</summary>
    private static string KeyText(object? value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    private static PropertyInfo Key(CassandraUnitOfWork unitOfWork, Type type)
    {
        var configuration = unitOfWork.ConfigurationOf(type)
                            ?? throw new InvalidOperationException($"'{type.Name}' is not registered in the Cassandra model.");
        return type.GetProperty(configuration.MemberFor(configuration.KeyColumn), Members)
               ?? throw new InvalidOperationException($"'{type.Name}' has no member for its key column '{configuration.KeyColumn}'.");
    }

    /// <summary>The foreign-key member on <paramref name="type" /> naming <paramref name="referenced" />: <c>[ForeignKey]</c>, then <c>{Name}Id</c>.</summary>
    private static PropertyInfo? ForeignKey(Type type, string referenced) =>
        type.GetProperties(Members).FirstOrDefault(property =>
            property.GetCustomAttribute<ForeignKeyAttribute>()?.Name.Equals(referenced, StringComparison.OrdinalIgnoreCase) == true)
        ?? type.GetProperties(Members).FirstOrDefault(property =>
            property.Name.Equals($"{referenced}Id", StringComparison.OrdinalIgnoreCase));

    private sealed class MemberCollector(ParameterExpression parameter) : ExpressionVisitor
    {
        private readonly HashSet<string> _members = new(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<string> Collect(Expression body)
        {
            Visit(body);
            return _members;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression == parameter)
            {
                _members.Add(node.Member.Name);
                return node;
            }

            return base.VisitMember(node);
        }
    }
}
