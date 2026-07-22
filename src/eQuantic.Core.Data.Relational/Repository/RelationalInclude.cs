using System.Collections;
using System.Reflection;

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
/// </summary>
internal static class RelationalInclude
{
    private const BindingFlags Members = BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance;

    /// <summary>Loads every include path into the materialized entities.</summary>
    public static async Task ApplyAsync<TEntity>(RelationalUnitOfWork unitOfWork, List<TEntity> entities,
        IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
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
            foreach (var segment in path.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                (current, currentType) = await ApplySegmentAsync(unitOfWork, current, currentType, segment, cancellationToken)
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
        CancellationToken cancellationToken)
    {
        var navigation = entityType.GetProperty(segment, Members)
                         ?? throw new NotSupportedException(
                             $"Cannot include '{segment}': '{entityType.Name}' has no such navigation property.");

        return ElementType(navigation.PropertyType) is { } elementType
            ? (await CollectionAsync(unitOfWork, entities, entityType, navigation, elementType, cancellationToken).ConfigureAwait(false), elementType)
            : (await ReferenceAsync(unitOfWork, entities, entityType, navigation, cancellationToken).ConfigureAwait(false), navigation.PropertyType);
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
        IReadOnlyList<object> entities, Type entityType, PropertyInfo navigation, Type elementType,
        CancellationToken cancellationToken)
    {
        var configuration = unitOfWork.Model.For(elementType);
        var parentConfiguration = unitOfWork.Model.For(entityType);

        // The declared navigation wins; the {Entity}Id convention covers the rest.
        var foreignKeyName = parentConfiguration.NavigationFor(navigation.Name)?.ForeignKey ?? entityType.Name + "Id";
        var foreignKey = configuration.ColumnFor(foreignKeyName)
                         ?? throw new NotSupportedException(
                             $"Cannot include '{navigation.Name}': no '{foreignKeyName}' property found on '{elementType.Name}'.");

        var keys = entities.Select(parentConfiguration.Key.Property.GetValue).Where(key => key is not null).Distinct().ToList();
        if (keys.Count == 0)
        {
            return [];
        }

        var elements = await LoadAsync(unitOfWork, configuration, foreignKey, keys, cancellationToken).ConfigureAwait(false);
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
