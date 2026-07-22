using System.Collections;
using System.Reflection;

namespace eQuantic.Core.Data.Relational.Repository;

/// <summary>
///     Loads <c>QueryOptions.Include</c> navigation paths with <b>one follow-up IN query per path</b> — no join
///     explosion, no N+1. Two shapes are supported, resolved by convention over the registered model:
///     <list type="bullet">
///         <item><b>reference</b> (<c>x =&gt; x.Customer</c>): the entity holds <c>{Nav}Id</c>, matched to the
///         referenced entity's key; the match populates the navigation.</item>
///         <item><b>collection</b> (<c>x =&gt; x.Items</c>): the element entities hold <c>{Entity}Id</c> back to
///         the entity's key; the matches populate the navigation list.</item>
///     </list>
///     Both entity types must be registered in the <see cref="RelationalModel" />.
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
            if (!string.IsNullOrWhiteSpace(path))
            {
                await ApplyPathAsync(unitOfWork, entities, path.Trim(), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task ApplyPathAsync<TEntity>(RelationalUnitOfWork unitOfWork, List<TEntity> entities,
        string path, CancellationToken cancellationToken)
        where TEntity : class
    {
        var navigation = typeof(TEntity).GetProperty(path, Members)
                         ?? throw new NotSupportedException(
                             $"Cannot include '{path}': '{typeof(TEntity).Name}' has no such navigation property.");

        if (ElementType(navigation.PropertyType) is { } elementType)
        {
            await CollectionAsync(unitOfWork, entities, navigation, elementType, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ReferenceAsync(unitOfWork, entities, navigation, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReferenceAsync<TEntity>(RelationalUnitOfWork unitOfWork, List<TEntity> entities,
        PropertyInfo navigation, CancellationToken cancellationToken)
        where TEntity : class
    {
        var configuration = unitOfWork.Model.For(navigation.PropertyType);
        var foreignKey = typeof(TEntity).GetProperty(navigation.Name + "Id", Members)
                         ?? throw new NotSupportedException(
                             $"Cannot include '{navigation.Name}': no '{navigation.Name}Id' property found on '{typeof(TEntity).Name}'.");

        var ids = entities.Select(entity => foreignKey.GetValue(entity)).Where(id => id is not null).Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
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
    }

    private static async Task CollectionAsync<TEntity>(RelationalUnitOfWork unitOfWork, List<TEntity> entities,
        PropertyInfo navigation, Type elementType, CancellationToken cancellationToken)
        where TEntity : class
    {
        var configuration = unitOfWork.Model.For(elementType);
        var parentConfiguration = unitOfWork.Configuration<TEntity>();
        var foreignKeyName = typeof(TEntity).Name + "Id";
        var foreignKey = configuration.ColumnFor(foreignKeyName)
                         ?? throw new NotSupportedException(
                             $"Cannot include '{navigation.Name}': no '{foreignKeyName}' property found on '{elementType.Name}'.");

        var keys = entities.Select(entity => parentConfiguration.Key.Property.GetValue(entity)).Where(key => key is not null).Distinct().ToList();
        if (keys.Count == 0)
        {
            return;
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
