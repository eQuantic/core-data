using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using eQuantic.Core.Data.Query;
using eQuantic.Core.Data.Repository.Options;

namespace eQuantic.Core.Data.Relational.Repository;

/// <summary>
///     Loads <c>QueryOptions.Include</c> navigation paths with <b>one follow-up IN query per path segment</b> —
///     no join explosion, no N+1. Two shapes are supported, resolved from the model's declared navigations
///     (<c>Reference(...)</c>/<c>Collection(...)</c> foreign-key overrides) or by convention:
///     <list type="bullet">
///         <item><b>reference</b> (<c>x =&gt; x.Customer</c>): the entity holds the foreign key
///         (<c>{Nav}Id</c> by convention), matched to the referenced entity's key.</item>
///         <item><b>collection</b> (<c>x =&gt; x.Items</c>): the elements hold the foreign key
///         (<c>{Entity}Id</c> by convention) back to the entity's key.</item>
///     </list>
///     A dotted path (<c>"Items.Product"</c>) loads level by level — each segment costs one query over the
///     previous segment's results. Both entity types must be registered in the <see cref="RelationalModel" />.
///     A shaped collection include (<c>Include(x =&gt; x.Steps, s =&gt; s.Where(…).OrderBy(…).Take(3))</c>) puts its
///     filter in the follow-up query's <c>WHERE</c> and its order in the <c>ORDER BY</c>; a skip or a limit per
///     parent ranks each parent's rows with <c>ROW_NUMBER() OVER (PARTITION BY fk ORDER BY …)</c>, so only the rows
///     each parent keeps travel.
/// </summary>
internal static class RelationalInclude
{
    private const BindingFlags Members = BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance;

    /// <summary>Loads every include path into the materialized entities.</summary>
    public static async Task ApplyAsync<TEntity>(RelationalUnitOfWork unitOfWork, List<TEntity> entities,
        IReadOnlyCollection<string> paths, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, IncludeShape>? shapes = null)
        where TEntity : class
    {
        if (entities.Count == 0)
        {
            return;
        }

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            IReadOnlyList<object> current = entities;
            var currentType = typeof(TEntity);
            var depth = 0;
            foreach (var segment in path.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                // A shape belongs to a navigation of the read entity itself: the first segment of a path.
                var shape = depth++ == 0 && shapes is not null && shapes.TryGetValue(segment, out var declared) ? declared : null;
                (current, currentType) = await ApplySegmentAsync(unitOfWork, current, currentType, segment, shape, cancellationToken)
                    .ConfigureAwait(false);
                if (current.Count == 0)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Loads one path segment into the current level's entities; returns the loaded targets for the next level.</summary>
    private static async Task<(IReadOnlyList<object> Targets, Type Type)> ApplySegmentAsync(
        RelationalUnitOfWork unitOfWork, IReadOnlyList<object> entities, Type entityType, string segment,
        IncludeShape? shape, CancellationToken cancellationToken)
    {
        var navigation = entityType.GetProperty(segment, Members)
                         ?? throw new NotSupportedException(
                             $"Cannot include '{segment}': '{entityType.Name}' has no such navigation property.");

        if (ElementType(navigation.PropertyType) is { } elementType)
        {
            return (await CollectionAsync(unitOfWork, entities, entityType, navigation, elementType, shape, cancellationToken)
                .ConfigureAwait(false), elementType);
        }

        if (shape is not null)
        {
            throw new NotSupportedException(
                $"Cannot shape the include '{segment}': a filter, an order or a limit applies to a collection, and it is a reference.");
        }

        return (await ReferenceAsync(unitOfWork, entities, entityType, navigation, cancellationToken).ConfigureAwait(false),
            navigation.PropertyType);
    }

    /// <summary>
    ///     Resolves a collection navigation: the element's mapping, the parent's mapping and the element's foreign-key
    ///     column back to the parent's key — the declared navigation first, the <c>{Entity}Id</c> convention after.
    /// </summary>
    internal static (RelationalEntityConfiguration Element, RelationalEntityConfiguration Parent, RelationalColumn ForeignKey)
        Collection(RelationalModel model, Type entityType, PropertyInfo navigation, Type elementType)
    {
        var configuration = model.For(elementType);
        var parentConfiguration = model.For(entityType);
        var foreignKeyName = parentConfiguration.NavigationFor(navigation.Name)?.ForeignKey ?? entityType.Name + "Id";
        var foreignKey = configuration.ColumnFor(foreignKeyName)
                         ?? throw new NotSupportedException(
                             $"Cannot load '{navigation.Name}': no '{foreignKeyName}' property found on '{elementType.Name}'.");
        return (configuration, parentConfiguration, foreignKey);
    }

    private static async Task<IReadOnlyList<object>> ReferenceAsync(RelationalUnitOfWork unitOfWork,
        IReadOnlyList<object> entities, Type entityType, PropertyInfo navigation, CancellationToken cancellationToken)
    {
        var configuration = unitOfWork.Model.For(navigation.PropertyType);
        if (configuration.HasCompositeKey)
        {
            throw new NotSupportedException(
                $"Cannot include '{navigation.Name}': '{navigation.PropertyType.Name}' has a composite key, which a " +
                "single foreign-key column cannot address — load it explicitly with its key tuple.");
        }

        // The declared navigation wins; the {Nav}Id convention covers the rest.
        var foreignKeyName = unitOfWork.Model.For(entityType).NavigationFor(navigation.Name)?.ForeignKey
                             ?? navigation.Name + "Id";
        var foreignKey = entityType.GetProperty(foreignKeyName, Members)
                         ?? throw new NotSupportedException(
                             $"Cannot include '{navigation.Name}': no '{foreignKeyName}' property found on '{entityType.Name}'.");

        var ids = entities.Select(foreignKey.GetValue).Where(id => id is not null).Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var referenced = await LoadAsync(unitOfWork, configuration, configuration.Key, ids, cancellationToken).ConfigureAwait(false);
        var byKey = referenced.ToDictionary(entity => configuration.Key.Property.GetValue(entity)!);

        foreach (var entity in entities)
        {
            if (foreignKey.GetValue(entity) is { } id && byKey.TryGetValue(id, out var match))
            {
                navigation.SetValue(entity, match);
            }
        }

        return referenced;
    }

    private static async Task<IReadOnlyList<object>> CollectionAsync(RelationalUnitOfWork unitOfWork,
        IReadOnlyList<object> entities, Type entityType, PropertyInfo navigation, Type elementType, IncludeShape? shape,
        CancellationToken cancellationToken)
    {
        var (configuration, parentConfiguration, foreignKey) = Collection(unitOfWork.Model, entityType, navigation, elementType);

        var keys = entities.Select(parentConfiguration.Key.Property.GetValue).Where(key => key is not null).Distinct().ToList();
        if (keys.Count == 0)
        {
            return [];
        }

        var elements = shape is null
            ? await LoadAsync(unitOfWork, configuration, foreignKey, keys, cancellationToken).ConfigureAwait(false)
            : await LoadShapedAsync(unitOfWork, configuration, foreignKey, keys, shape, cancellationToken).ConfigureAwait(false);

        // GroupBy keeps the rows' order within each group, so a shaped include's order survives the stitching.
        var byParent = elements.GroupBy(element => foreignKey.Property.GetValue(element)!)
            .ToDictionary(group => group.Key, group => group.ToList());

        foreach (var entity in entities)
        {
            var key = parentConfiguration.Key.Property.GetValue(entity);
            var matches = key is not null && byParent.TryGetValue(key, out var found) ? found : [];

            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
            foreach (var match in matches)
            {
                list.Add(match);
            }

            navigation.SetValue(entity, list);
        }

        return elements;
    }

    /// <summary>
    ///     Loads a shaped collection: the shape's filter joins the <c>IN</c> in the <c>WHERE</c>, its order is the
    ///     <c>ORDER BY</c>, and a skip or a limit ranks each parent's rows with <c>ROW_NUMBER()</c> partitioned by the
    ///     foreign key, keeping the ranks the window allows. Only the rows a parent keeps leave the database.
    /// </summary>
    private static async Task<List<object>> LoadShapedAsync(RelationalUnitOfWork unitOfWork,
        RelationalEntityConfiguration configuration, RelationalColumn foreignKey, IReadOnlyList<object?> keys,
        IncludeShape shape, CancellationToken cancellationToken)
    {
        var dialect = unitOfWork.Dialect;
        var parameters = keys.Select(dialect.BindValue).ToList();
        var placeholders = string.Join(", ", Enumerable.Range(0, parameters.Count).Select(index => "@p" + index));
        var where = $"{dialect.Quote(foreignKey.Name)} IN ({placeholders})";
        if (shape.Filter is not null)
        {
            where += " AND " + SqlFilterRenderer.Render(dialect, configuration, FilterInterpreter.Interpret(shape.Filter), parameters);
        }

        var order = shape.Sortings.Count > 0
            ? string.Join(", ", shape.Sortings.Select(sort => dialect.OrderTerm(
                dialect.Quote(SortColumn(configuration, sort.KeySelector).Name), sort.Descending, sort.Nulls)))
            : string.Join(", ", configuration.Keys.Select(key => dialect.Quote(key.Name) + " ASC"));
        var columns = string.Join(", ", configuration.Columns.Select(column => dialect.Quote(column.Name)));
        var table = dialect.Quote(configuration.TableName);

        string sql;
        if (shape.SkipCount is null && shape.TakeCount is null)
        {
            sql = $"SELECT {columns} FROM {table} WHERE {where} ORDER BY {order}";
        }
        else
        {
            var rank = dialect.Quote("eq_rank");
            var skip = shape.SkipCount ?? 0;
            parameters.Add(skip);
            var bounds = $"{rank} > @p{parameters.Count - 1}";
            if (shape.TakeCount is { } take)
            {
                parameters.Add(skip + take);
                bounds += $" AND {rank} <= @p{parameters.Count - 1}";
            }

            sql = $"SELECT {columns} FROM (SELECT {columns}, ROW_NUMBER() OVER (PARTITION BY {dialect.Quote(foreignKey.Name)} " +
                  $"ORDER BY {order}) AS {rank} FROM {table} WHERE {where}) {dialect.Quote("eq_ranked")} " +
                  $"WHERE {bounds} ORDER BY {rank}";
        }

        await using var command = await unitOfWork.CommandAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var loaded = new List<object>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            loaded.Add(RelationalMaterializer.Materialize(configuration.EntityType, reader, configuration.Columns));
        }

        return loaded;
    }

    /// <summary>The column a sort of a related entity names (<c>s =&gt; s.StartedAt</c>).</summary>
    internal static RelationalColumn SortColumn(RelationalEntityConfiguration configuration, LambdaExpression selector)
    {
        var body = selector.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            body = unary.Operand;
        }

        return body is MemberExpression { Expression: ParameterExpression } member
               && configuration.ColumnFor(member.Member.Name) is { } column
            ? column
            : throw new NotSupportedException(
                $"'{configuration.EntityType.Name}' can only be ordered by a mapped member; '{selector}' is not one.");
    }

    private static async Task<List<object>> LoadAsync(RelationalUnitOfWork unitOfWork,
        RelationalEntityConfiguration configuration, RelationalColumn filterColumn, IReadOnlyList<object?> values,
        CancellationToken cancellationToken)
    {
        var dialect = unitOfWork.Dialect;
        var parameters = values.Select(dialect.BindValue).ToList();
        var placeholders = string.Join(", ", Enumerable.Range(0, parameters.Count).Select(index => "@p" + index));
        var sql = $"SELECT {string.Join(", ", configuration.Columns.Select(column => dialect.Quote(column.Name)))}"
                  + $" FROM {dialect.Quote(configuration.TableName)} WHERE {dialect.Quote(filterColumn.Name)} IN ({placeholders})";

        await using var command = await unitOfWork.CommandAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var loaded = new List<object>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            loaded.Add(RelationalMaterializer.Materialize(configuration.EntityType, reader, configuration.Columns));
        }

        return loaded;
    }

    private static Type? ElementType(Type type)
    {
        if (type == typeof(string) || type == typeof(byte[]))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        return type.GetInterfaces()
            .Append(type)
            .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }
}
