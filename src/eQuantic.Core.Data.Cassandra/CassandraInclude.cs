using System.Collections;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;
using System.Reflection;
using eQuantic.Core.Data.Cassandra.Repository;
using eQuantic.Core.Data.Query;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Linq.Expressions;

namespace eQuantic.Core.Data.Cassandra;

/// <summary>
///     Loads related rows from their own tables. Cassandra rows are self-contained and CQL has no join, so each
///     <c>Include</c> segment is <b>a second query</b> over the related table — the parents' keys resolved and read
///     with <c>IN</c>, in chunks of <see cref="CassandraCqlRenderer.InChunkSize" /> keys — under the same gates as any read
///     and the read's own opt-ins: <c>.AllowFiltering()</c> when the read is not scoped to the related table's partitions
///     (its foreign key is not the partition key, so the table is scanned), <c>.AllowClientEvaluation()</c> for a filter
///     CQL cannot express (a foreign key outside the primary key among them: CQL takes <c>IN</c> on the primary key only).
///     A shape's order, skip and limit apply per parent as the rows are stitched; when the foreign key is the
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
        var configuration = await ReadConfigurationAsync(unitOfWork, typeof(TElement)).ConfigureAwait(false);
        var perPartitionLimit = PerPartitionLimit(configuration, foreignKey, shape);
        var elements = await InChunksAsync(configuration, foreignKey, keys, chunk => ReadAsync<TElement>(unitOfWork, configuration,
            In<TElement>(foreignKey, chunk), shape?.Filter as Expression<Func<TElement, bool>>, options, $"the include of '{navigation.Name}'",
            perPartitionLimit)).ConfigureAwait(false);

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
        var configuration = await ReadConfigurationAsync(unitOfWork, typeof(TTarget)).ConfigureAwait(false);
        var targetKey = Key(unitOfWork, typeof(TTarget));
        var ids = parents.Select(localKey.GetValue).Where(value => value is not null).Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var targets = await InChunksAsync(configuration, targetKey, ids, chunk => ReadAsync<TTarget>(unitOfWork, configuration,
            In<TTarget>(targetKey, chunk), null, options, $"the include of '{navigation.Name}'", null)).ConfigureAwait(false);
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

        var configuration = await ReadConfigurationAsync(unitOfWork, typeof(TElement)).ConfigureAwait(false);
        var foreignKey = ForeignKey(typeof(TElement), typeof(TParent).Name)
                         ?? throw new NotSupportedException(
                             $"Cannot count '{count.Navigation.Name}': no foreign key found on '{typeof(TElement).Name}' back to '{typeof(TParent).Name}'.");
        var column = configuration.ColumnFor(foreignKey.Name);
        var options = Options<TElement>(parentOptions, count.Predicate as Expression<Func<TElement, bool>>);
        var plan = CassandraCql.Plan(configuration, options, In<TElement>(foreignKey, keys.Take(CassandraCqlRenderer.InChunkSize).ToList()));
        var consistency = CassandraQueryOptionsExtensions.ConsistencyOf(parentOptions);

        if (plan.Residual.Count == 0 && plan.Alternatives.Count == 0
            && configuration.PartitionKeys is [var partition] && CassandraEntityConfiguration.Same(partition, column))
        {
            // The foreign key is the partition key: the cluster groups and counts, only the counts travel.
            Gate(plan, options, $"the count of '{count.Navigation.Name}'", clientWork: false);
            // The key comes back in its stored form (an enum as its number or label): read it as the mapper reads a
            // member, so it names the parent as the parent's own key does.
            var keyColumn = configuration.Column(column);
            var keyType = keyColumn is null ? typeof(object) : CassandraValues.StoredType(keyColumn, foreignKey.PropertyType);
            var grouped = await InChunksAsync(configuration, foreignKey, keys, async chunk =>
            {
                var chunkPlan = CassandraCql.Plan(configuration, options, In<TElement>(foreignKey, chunk));
                var cql = $"SELECT {column}, COUNT(*) FROM {configuration.TableName} WHERE {chunkPlan.Where} GROUP BY {column}"
                          + (chunkPlan.RequiresAllowFiltering ? " ALLOW FILTERING" : string.Empty);
                var rows = await CassandraStatements.ExecuteAsync(unitOfWork.GetSession(), cql, chunkPlan.Values, consistency,
                    unitOfWork.CommandLogger, unitOfWork.SensitiveLogging).ConfigureAwait(false);
                return rows.Select(row => (Key: KeyText(CassandraValues.FromStored(foreignKey.PropertyType, row.GetValue(keyType, 0))),
                    Count: row.GetValue<long>(1))).ToList();
            }).ConfigureAwait(false);
            foreach (var (key, found) in grouped)
            {
                counts[key] = found;
            }

            return counts;
        }

        // Counted client-side: only the foreign key and the columns a client-side filter reads travel.
        Gate(plan, options, $"the count of '{count.Navigation.Name}'", clientWork: true);
        var elements = await InChunksAsync(configuration, foreignKey, keys, chunk => ReadAsync<TElement>(unitOfWork, configuration,
            In<TElement>(foreignKey, chunk), options.Filter, parentOptions, $"the count of '{count.Navigation.Name}'", null,
            [column, .. ResidualColumns(configuration, plan)])).ConfigureAwait(false);
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
        var cql = $"SELECT {(selected is null ? "*" : string.Join(", ", selected))} FROM {configuration.TableName}"
                  + (plan.Where.Length > 0 ? $" WHERE {plan.Where}" : string.Empty)
                  + (push ? $" PER PARTITION LIMIT {perPartitionLimit}" : string.Empty)
                  + (Filtering(plan) ? " ALLOW FILTERING" : string.Empty);
        var rows = await CassandraStatements.ExecuteAsync(unitOfWork.GetSession(), cql, plan.Values,
            CassandraQueryOptionsExtensions.ConsistencyOf(parentOptions), unitOfWork.CommandLogger, unitOfWork.SensitiveLogging)
            .ConfigureAwait(false);

        var residuals = plan.Residual.Select(PredicateNormalizer.CompileResidual<TElement>).ToList();
        return rows.Select(row => CassandraMapper.Materialize<TElement>(configuration, row, selected))
            .Where(element => residuals.All(residual => residual(element)))
            .ToList();
    }

    /// <summary>
    ///     The distinct values of a member of the related rows a relation test reads: <c>SELECT DISTINCT</c> of the
    ///     partition key when the member is the table's partition key and nothing filters it; otherwise the member (and
    ///     what a client-side filter reads) of the rows the condition matches, deduplicated client-side — through the same
    ///     planner and gates as any read, under the read's opt-ins. A read not scoped to the related table's partitions (no
    ///     condition, or one off the partition key) scans the table, so a read makes it only behind <c>.AllowFiltering()</c>.
    ///     A write carries no opt-ins: its related read has to be partition-scoped, by the condition or by
    ///     <paramref name="scope" /> — the keys of the rows the write finds, read from the related table by the member, in
    ///     chunks, which is partition-scoped when the member is the related table's partition key.
    /// </summary>
    /// <param name="unitOfWork">The unit of work.</param>
    /// <param name="condition">The condition the related rows meet (already resolved), or <c>null</c> for every row.</param>
    /// <param name="member">The member of the related rows whose distinct values are read.</param>
    /// <param name="parentOptions">The read's options (its opt-ins and consistency); <c>null</c> for a write.</param>
    /// <param name="what">What reads, for messages.</param>
    /// <param name="write">Whether a set-based write reads (no opt-ins, and no scan whatever they say).</param>
    /// <param name="scope">
    ///     For a write: the values of <paramref name="member" /> the rows the write finds can be related by, or <c>null</c>
    ///     when the write's filter does not name its partition.
    /// </param>
    public static async Task<IReadOnlyCollection<object?>> RelatedKeysAsync<TTarget>(CassandraUnitOfWork unitOfWork,
        Expression<Func<TTarget, bool>>? condition, PropertyInfo member, object? parentOptions, string what, bool write = false,
        Func<Task<IReadOnlyCollection<object?>?>>? scope = null)
        where TTarget : class
    {
        var configuration = await ReadConfigurationAsync(unitOfWork, typeof(TTarget)).ConfigureAwait(false);
        var column = configuration.ColumnFor(member.Name);
        var options = Options(parentOptions, condition);
        var plan = CassandraCql.Plan(configuration, options, null, unitOfWork.GlobalFilter<TTarget>());
        var consistency = CassandraQueryOptionsExtensions.ConsistencyOf(parentOptions);

        if (write && !plan.PartitionScoped && scope is not null && await scope().ConfigureAwait(false) is { } keys)
        {
            // The related rows of the rows the write found: read by the member, a chunk of their keys at a time.
            var related = new List<object?>();
            foreach (var chunk in keys.Chunk(CassandraCqlRenderer.InChunkSize))
            {
                var scoped = In<TTarget>(member, chunk);
                related.AddRange(await RelatedKeysAsync(unitOfWork, condition is null ? scoped : condition.AndAlso(scoped), member, null,
                    what, write: true).ConfigureAwait(false));
            }

            return related.Distinct().ToList();
        }

        Gate(plan, options, what, clientWork: false, write ? configuration.TableName : null);
        if (plan.Where.Length == 0 && plan.Residual.Count == 0 && plan.Alternatives.Count == 0
            && configuration.PartitionKeys is [var partition] && CassandraEntityConfiguration.Same(partition, column))
        {
            var distinct = await CassandraStatements.ExecuteAsync(unitOfWork.GetSession(),
                $"SELECT DISTINCT {column} FROM {configuration.TableName}", [], consistency, unitOfWork.CommandLogger,
                unitOfWork.SensitiveLogging).ConfigureAwait(false);
            var only = new HashSet<string>([column], StringComparer.OrdinalIgnoreCase);
            return distinct.Select(row => CassandraMapper.Materialize<TTarget>(configuration, row, only))
                .Select(target => member.GetValue(target)).Distinct().ToList();
        }

        var selected = new HashSet<string>([column, .. ResidualColumns(configuration, plan)], StringComparer.OrdinalIgnoreCase);
        var cql = $"SELECT {string.Join(", ", selected)} FROM {configuration.TableName}"
                  + (plan.Where.Length > 0 ? $" WHERE {plan.Where}" : string.Empty)
                  + (Filtering(plan) ? " ALLOW FILTERING" : string.Empty);
        var rows = await CassandraStatements.ExecuteAsync(unitOfWork.GetSession(), cql, plan.Values, consistency,
            unitOfWork.CommandLogger, unitOfWork.SensitiveLogging).ConfigureAwait(false);
        var residuals = plan.Residual.Select(PredicateNormalizer.CompileResidual<TTarget>).ToList();
        return rows.Select(row => CassandraMapper.Materialize<TTarget>(configuration, row, selected))
            .Where(target => residuals.All(residual => residual(target)))
            .Select(target => member.GetValue(target))
            .Distinct()
            .ToList();
    }

    /// <summary>
    ///     Whether a related read's CQL carries <c>ALLOW FILTERING</c>: a clause outside the primary key, or a restriction
    ///     that does not name the partition (a clustering column alone) — either way a scan its gate let through.
    /// </summary>
    private static bool Filtering(CassandraCqlPlan plan) =>
        plan.RequiresAllowFiltering || (!plan.PartitionScoped && plan.Where.Length > 0);

    /// <summary>
    ///     The read's gates, applied to a related read: a scan — a clause outside the primary key, or a read not scoped to the
    ///     related table's partitions — needs <c>.AllowFiltering()</c>, client work <c>.AllowClientEvaluation()</c>. A set-based
    ///     write (<paramref name="writeTable" /> names the related table) carries no opt-ins, so its related read has to need
    ///     none.
    /// </summary>
    private static void Gate<TElement>(CassandraCqlPlan plan, QueryOptions<TElement> options, string what, bool clientWork,
        string? writeTable = null)
        where TElement : class
    {
        if (plan.Alternatives.Count > 0)
        {
            throw new NotSupportedException(
                $"{Capitalized(what)} cannot split an OR into several queries; restructure its filter around the keys.");
        }

        if (writeTable is not null)
        {
            if (!plan.PartitionScoped || plan.RequiresAllowFiltering || plan.Residual.Count > 0 || clientWork)
            {
                throw new NotSupportedException(
                    $"{Capitalized(what)} would read '{writeTable}' " +
                    (!plan.PartitionScoped ? "whole (it names none of its partitions)" : "with a filter CQL cannot serve natively") +
                    ", and a set-based write carries no opt-ins. Give the relation a condition on the related table's partition key, " +
                    "or name the write's partition key so the related rows are read for the rows the write finds — by the related " +
                    "table's partition key, which has to be the relation's key.");
            }

            return;
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

        if (!plan.PartitionScoped && !CassandraCql.AllowFilteringOptedIn(options))
        {
            throw new NotSupportedException(
                $"{Capitalized(what)} names none of the related table's partitions, so it reads the whole table; call " +
                ".AllowFiltering() on the read to acknowledge the scan, or restrict the related rows by their partition key " +
                "(partition the related table by the foreign key).");
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
    ///     first rows of each partition are the ones each parent keeps. Not for a clustering column stored as an enum's
    ///     labels: the partition keeps it in the labels' order, and the shape asks for the members'.
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
            // An enum stored as its labels is clustered by the labels' text, while the shape orders the members' values.
            if (MemberOf(sort.KeySelector) is not { } member
                || !CassandraEntityConfiguration.Same(configuration.ColumnFor(member), clustering.Column)
                || sort.Descending != clustering.Descending
                || configuration.Column(clustering.Column) is { CqlType: "text" } stored
                && (Nullable.GetUnderlyingType(stored.ClrType) ?? stored.ClrType).IsEnum)
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

    /// <summary>
    ///     Reads related rows by a list of keys: a chunk of <see cref="CassandraCqlRenderer.InChunkSize" /> keys per query
    ///     when the key is a primary-key column (the <c>IN</c> pushes down), a few queries at a time, and in one read
    ///     otherwise (the <c>IN</c> runs client-side over the rows read, and one scan is enough).
    /// </summary>
    private static async Task<List<T>> InChunksAsync<T>(CassandraEntityConfiguration configuration, PropertyInfo member,
        IReadOnlyList<object?> keys, Func<IReadOnlyList<object?>, Task<List<T>>> read)
    {
        if (keys.Count == 0)
        {
            return [];
        }

        if (!configuration.IsKey(configuration.ColumnFor(member.Name)))
        {
            return await read(keys).ConfigureAwait(false);
        }

        using var slots = new SemaphoreSlim(ChunkQueries);
        var chunks = await Task.WhenAll(keys.Chunk(CassandraCqlRenderer.InChunkSize).Select(async chunk =>
        {
            await slots.WaitAsync().ConfigureAwait(false);
            try
            {
                return await read(chunk).ConfigureAwait(false);
            }
            finally
            {
                slots.Release();
            }
        })).ConfigureAwait(false);
        return chunks.SelectMany(chunk => chunk).ToList();
    }

    /// <summary>How many chunks of a related read run at once.</summary>
    private const int ChunkQueries = 8;

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

    /// <summary>
    ///     The mapping a related read renders with: a case-insensitive match pushes down only on an index the cluster holds
    ///     case-insensitive (<see cref="CassandraSearchIndexes" />).
    /// </summary>
    private static Task<CassandraEntityConfiguration> ReadConfigurationAsync(CassandraUnitOfWork unitOfWork, Type type) =>
        CassandraSearchIndexes.VerifiedAsync(unitOfWork.GetSession(),
            unitOfWork.ConfigurationOf(type) ?? throw new InvalidOperationException($"'{type.Name}' is not registered in the Cassandra model."));

    internal static PropertyInfo Key(CassandraUnitOfWork unitOfWork, Type type)
    {
        var configuration = unitOfWork.ConfigurationOf(type)
                            ?? throw new InvalidOperationException($"'{type.Name}' is not registered in the Cassandra model.");
        return type.GetProperty(configuration.MemberFor(configuration.KeyColumn), Members)
               ?? throw new InvalidOperationException($"'{type.Name}' has no member for its key column '{configuration.KeyColumn}'.");
    }

    /// <summary>The foreign-key member on <paramref name="type" /> naming <paramref name="referenced" />: <c>[ForeignKey]</c>, then <c>{Name}Id</c>.</summary>
    internal static PropertyInfo? ForeignKey(Type type, string referenced) =>
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
