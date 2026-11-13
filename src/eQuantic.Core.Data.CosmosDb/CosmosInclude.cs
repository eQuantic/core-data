using System.Collections;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;
using System.Reflection;
using eQuantic.Core.Data.CosmosDb.Repository;
using eQuantic.Core.Data.Query;
using eQuantic.Core.Data.Repository.Options;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;

namespace eQuantic.Core.Data.CosmosDb;

/// <summary>
///     Loads related documents from their own containers. Cosmos DB has no join across containers, so each
///     <c>Include</c> segment is <b>one second query</b> over the related container — the parents' keys resolved and
///     read with <c>IN</c>, the shape's filter pushed into its <c>WHERE</c> — and the rows are
///     stitched onto their parents. A shape's order, skip and limit apply per parent as the rows are grouped: every row
///     the filter keeps travels, the ones past each parent's limit are dropped. Relation counts run as one grouped
///     query per count (<c>SELECT fk, COUNT(1) … GROUP BY fk</c>), so only the counts travel.
///     <list type="bullet">
///         <item><b>collection</b> (<c>x =&gt; x.Steps</c>): the related documents hold the foreign key
///         (<c>{Parent}Id</c> by convention, or <c>[ForeignKey("Parent")]</c>) back to the parent's id.</item>
///         <item><b>reference</b> (<c>x =&gt; x.Customer</c>): the parent holds <c>{Nav}Id</c>, matched to the related id.</item>
///     </list>
///     Both entity types must be registered in the <see cref="CosmosModel" />.
/// </summary>
internal static class CosmosInclude
{
    private const BindingFlags Members = BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance;

    /// <summary>Loads every include path into the entities, level by level.</summary>
    public static async Task ApplyAsync<TEntity>(CosmosUnitOfWork unitOfWork, List<TEntity> entities,
        IReadOnlyCollection<string> paths, IReadOnlyDictionary<string, IncludeShape> shapes, CancellationToken cancellationToken)
        where TEntity : class
    {
        if (entities.Count == 0)
        {
            return;
        }

        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            IReadOnlyList<object> current = entities;
            var currentType = typeof(TEntity);
            var depth = 0;
            foreach (var segment in path.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var shape = depth++ == 0 && shapes.TryGetValue(segment, out var declared) ? declared : null;
                var navigation = currentType.GetProperty(segment, Members)
                                 ?? throw new NotSupportedException(
                                     $"Cannot include '{segment}': '{currentType.Name}' has no such navigation property.");

                if (RelationCountInterpreter.ElementType(navigation.PropertyType) is { } elementType)
                {
                    current = await Dispatch(nameof(CollectionAsync), [currentType, elementType],
                        unitOfWork, current, navigation, shape, cancellationToken).ConfigureAwait(false);
                    currentType = elementType;
                }
                else
                {
                    if (shape is not null)
                    {
                        throw new NotSupportedException(
                            $"Cannot shape the include '{segment}': a filter, an order or a limit applies to a collection, and it is a reference.");
                    }

                    current = await Dispatch(nameof(ReferenceAsync), [currentType, navigation.PropertyType],
                        unitOfWork, current, navigation, null, cancellationToken).ConfigureAwait(false);
                    currentType = navigation.PropertyType;
                }

                if (current.Count == 0)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    ///     Counts, for each entity, the related documents of each count: one grouped query per count over the related
    ///     container, keyed by the foreign key. Answers one array of counts per entity, in the entities' order.
    /// </summary>
    public static async Task<long[][]> CountAsync<TEntity>(CosmosUnitOfWork unitOfWork, IReadOnlyList<TEntity> entities,
        IReadOnlyList<RelationCount> counts, CancellationToken cancellationToken) where TEntity : class
    {
        var result = entities.Select(_ => new long[counts.Count]).ToArray();
        if (entities.Count == 0)
        {
            return result;
        }

        var key = Key(typeof(TEntity));
        for (var index = 0; index < counts.Count; index++)
        {
            var count = counts[index];
            var foreignKey = ForeignKey(count.ElementType, typeof(TEntity).Name)
                             ?? throw new NotSupportedException(
                                 $"Cannot count '{count.Navigation.Name}': no foreign key found on '{count.ElementType.Name}' back to '{typeof(TEntity).Name}'.");
            var perKey = (IReadOnlyDictionary<string, long>)(await ((Task<object>)typeof(CosmosInclude)
                    .GetMethod(nameof(GroupCountAsync), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(count.ElementType, foreignKey.PropertyType)
                    .Invoke(null, [unitOfWork, entities.Select(key.GetValue).Where(value => value is not null).Distinct().ToList(),
                        foreignKey, count.Predicate, cancellationToken])!).ConfigureAwait(false));

            for (var row = 0; row < entities.Count; row++)
            {
                result[row][index] = key.GetValue(entities[row]) is { } parent && perKey.TryGetValue(KeyText(parent), out var found) ? found : 0;
            }
        }

        return result;
    }

    /// <summary>Whether a member is a collection of documents of a registered entity holding a foreign key back to the parent.</summary>
    public static bool IsRelation(CosmosUnitOfWork unitOfWork, Type parentType, PropertyInfo member) =>
        RelationCountInterpreter.ElementType(member.PropertyType) is { IsClass: true } element
        && element != typeof(string)
        && unitOfWork.Maps(element)
        && ForeignKey(element, parentType.Name) is not null;

    // ---------------------------------------------------------------- typed cores

    private static async Task<IReadOnlyList<object>> CollectionAsync<TParent, TElement>(CosmosUnitOfWork unitOfWork,
        IReadOnlyList<object> parents, PropertyInfo navigation, IncludeShape? shape, CancellationToken cancellationToken)
        where TElement : class
    {
        var key = Key(typeof(TParent));
        var foreignKey = ForeignKey(typeof(TElement), typeof(TParent).Name)
                         ?? throw new NotSupportedException(
                             $"Cannot include '{navigation.Name}': no foreign key found on '{typeof(TElement).Name}' back to " +
                             $"'{typeof(TParent).Name}' (expected [ForeignKey(\"{typeof(TParent).Name}\")] or a '{typeof(TParent).Name}Id' property).");

        var keys = parents.Select(key.GetValue).Where(value => value is not null).Distinct().ToList();
        var query = unitOfWork.GetContainer<TElement>().GetItemLinqQueryable<TElement>()
            .Where(In<TElement>(foreignKey, keys));
        if (shape?.Filter is Expression<Func<TElement, bool>> filter)
        {
            query = query.Where(CosmosPredicates.Rendered(filter));
        }

        var elements = await ReadAllAsync(query, cancellationToken).ConfigureAwait(false);
        var byParent = elements.GroupBy(element => KeyText(foreignKey.GetValue(element)))
            .ToDictionary(group => group.Key, group => (IEnumerable<TElement>)group);

        var loaded = new List<object>();
        foreach (var parent in parents)
        {
            IEnumerable<TElement> related = key.GetValue(parent) is { } value && byParent.TryGetValue(KeyText(value), out var found)
                ? found
                : [];
            var kept = shape is null ? related.ToList() : ReadShapes.Page(related, shape);
            navigation.SetValue(parent, Shaped(kept, navigation.PropertyType));
            loaded.AddRange(kept);
        }

        return loaded;
    }

    private static async Task<IReadOnlyList<object>> ReferenceAsync<TParent, TTarget>(CosmosUnitOfWork unitOfWork,
        IReadOnlyList<object> parents, PropertyInfo navigation, IncludeShape? shape, CancellationToken cancellationToken)
        where TTarget : class
    {
        _ = shape;
        var localKey = ForeignKey(typeof(TParent), navigation.Name)
                       ?? throw new NotSupportedException(
                           $"Cannot include '{navigation.Name}': no '{navigation.Name}Id' property found on '{typeof(TParent).Name}'.");
        var targetKey = Key(typeof(TTarget));
        var ids = parents.Select(localKey.GetValue).Where(value => value is not null).Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var targets = await ReadAllAsync(unitOfWork.GetContainer<TTarget>().GetItemLinqQueryable<TTarget>()
            .Where(In<TTarget>(targetKey, ids)), cancellationToken).ConfigureAwait(false);
        var byKey = targets.ToDictionary(target => KeyText(targetKey.GetValue(target)));
        foreach (var parent in parents)
        {
            if (localKey.GetValue(parent) is { } id && byKey.TryGetValue(KeyText(id), out var match))
            {
                navigation.SetValue(parent, match);
            }
        }

        return targets;
    }

    /// <summary>
    ///     <c>SELECT root.fk AS k, COUNT(1) AS n FROM root WHERE root.fk IN (…) AND (filter) GROUP BY root.fk</c>:
    ///     the <c>WHERE</c> is the SDK's own translation of the filter, so member names and values render as on any read.
    /// </summary>
    private static async Task<object> GroupCountAsync<TElement, TKey>(CosmosUnitOfWork unitOfWork, List<object?> keys,
        PropertyInfo foreignKey, LambdaExpression? predicate, CancellationToken cancellationToken) where TElement : class
    {
        var counts = new Dictionary<string, long>();
        if (keys.Count == 0)
        {
            return counts;
        }

        var container = unitOfWork.GetContainer<TElement>();
        var query = container.GetItemLinqQueryable<TElement>().Where(In<TElement>(foreignKey, keys));
        if (predicate is Expression<Func<TElement, bool>> filter)
        {
            query = query.Where(CosmosPredicates.Rendered(filter));
        }

        var parameter = Expression.Parameter(typeof(TElement), "element");
        var definition = query.Select(Expression.Lambda<Func<TElement, TKey>>(Expression.Property(parameter, foreignKey), parameter))
            .ToQueryDefinition();

        // The SDK renders "SELECT VALUE <fk> FROM root WHERE <filter>": the grouped count keeps both parts.
        const string select = "SELECT VALUE ";
        var text = definition.QueryText;
        var from = text.IndexOf(" FROM root", StringComparison.Ordinal);
        if (!text.StartsWith(select, StringComparison.Ordinal) || from < 0)
        {
            throw new NotSupportedException(
                $"Cannot count '{typeof(TElement).Name}' on the server: the SDK translated the filter as '{text}', which the grouped count does not recognise.");
        }

        var member = text[select.Length..from];
        var grouped = new QueryDefinition($"SELECT {member} AS k, COUNT(1) AS n{text[from..]} GROUP BY {member}");
        foreach (var (name, value) in definition.GetQueryParameters())
        {
            grouped.WithParameter(name, value);
        }

        using var iterator = container.GetItemQueryIterator<GroupedCount<TKey>>(grouped);
        while (iterator.HasMoreResults)
        {
            foreach (var row in await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                counts[KeyText(row.K)] = row.N;
            }
        }

        return counts;
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<IReadOnlyList<object>> Dispatch(string method, Type[] typeArguments, CosmosUnitOfWork unitOfWork,
        IReadOnlyList<object> parents, PropertyInfo navigation, IncludeShape? shape, CancellationToken cancellationToken) =>
        await ((Task<IReadOnlyList<object>>)typeof(CosmosInclude)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeArguments)
            .Invoke(null, [unitOfWork, parents, navigation, shape, cancellationToken])!).ConfigureAwait(false);

    /// <summary>Builds <c>x =&gt; keys.Contains(x.Member)</c>, which the SDK renders as <c>IN (…)</c>.</summary>
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

    private static async Task<List<T>> ReadAllAsync<T>(IQueryable<T> query, CancellationToken cancellationToken)
    {
        var results = new List<T>();
        using var iterator = query.ToFeedIterator();
        while (iterator.HasMoreResults)
        {
            results.AddRange(await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private static object Shaped<TElement>(List<TElement> kept, Type navigationType) =>
        navigationType.IsArray ? kept.ToArray() : kept;

    /// <summary>A key compared by its text, so a foreign key and the id it names match whatever their CLR types.</summary>
    private static string KeyText(object? value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    private static PropertyInfo Key(Type type) =>
        type.GetProperty("Id", Members)
        ?? throw new NotSupportedException($"'{type.Name}' has no 'Id' member: a Cosmos document is addressed by its id.");

    /// <summary>The foreign-key member on <paramref name="type" /> naming <paramref name="referenced" />: <c>[ForeignKey]</c>, then <c>{Name}Id</c>.</summary>
    public static PropertyInfo? ForeignKey(Type type, string referenced) =>
        type.GetProperties(Members).FirstOrDefault(property =>
            property.GetCustomAttribute<ForeignKeyAttribute>()?.Name.Equals(referenced, StringComparison.OrdinalIgnoreCase) == true)
        ?? type.GetProperties(Members).FirstOrDefault(property =>
            property.Name.Equals($"{referenced}Id", StringComparison.OrdinalIgnoreCase));

    private sealed class GroupedCount<TKey>
    {
        public TKey K { get; set; } = default!;

        public long N { get; set; }
    }
}
