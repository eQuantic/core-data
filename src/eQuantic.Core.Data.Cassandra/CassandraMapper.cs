using System.Collections.Concurrent;
using System.Reflection;
using global::Cassandra;

namespace eQuantic.Core.Data.Cassandra;

/// <summary>
///     Builds CQL statements and materializes rows for an entity from its <see cref="CassandraEntityConfiguration" />,
///     by reflection — so domain entities stay free of driver attributes. A Cassandra <c>INSERT</c> is an upsert by
///     primary key, so both <c>Add</c> and <c>Modify</c> map to it.
/// </summary>
internal static class CassandraMapper
{
    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> Properties = new();

    /// <summary>Builds an upsert (<c>INSERT</c>) for the full row.</summary>
    public static (string Cql, object?[] Values) BuildUpsert(CassandraEntityConfiguration configuration, object entity)
    {
        var columns = configuration.Columns;
        var names = string.Join(", ", columns.Select(column => column.Name));
        var placeholders = string.Join(", ", columns.Select(_ => "?"));
        var values = columns.Select(column => Read(entity, column.Name)).ToArray();
        return ($"INSERT INTO {configuration.TableName} ({names}) VALUES ({placeholders})", values);
    }

    /// <summary>Builds a <c>DELETE</c> targeting the row's full primary key.</summary>
    public static (string Cql, object?[] Values) BuildDelete(CassandraEntityConfiguration configuration, object entity)
    {
        var keys = PrimaryKey(configuration);
        var where = string.Join(" AND ", keys.Select(key => $"{key} = ?"));
        var values = keys.Select(key => Read(entity, key)).ToArray();
        return ($"DELETE FROM {configuration.TableName} WHERE {where}", values);
    }

    /// <summary>Materializes a row into a new <typeparamref name="TEntity" />.</summary>
    public static TEntity Materialize<TEntity>(CassandraEntityConfiguration configuration, Row row)
    {
        var entity = Activator.CreateInstance<TEntity>()!;
        foreach (var column in configuration.Columns)
        {
            var property = Property(typeof(TEntity), column.Name);
            if (property is null || !property.CanWrite || row.IsNull(column.Name))
            {
                continue;
            }

            property.SetValue(entity, row.GetValue(property.PropertyType, column.Name));
        }

        return entity;
    }

    /// <summary>The primary key columns (partition key then clustering keys), in order.</summary>
    public static IReadOnlyList<string> PrimaryKey(CassandraEntityConfiguration configuration) =>
        configuration.PartitionKeys.Concat(configuration.ClusteringKeys.Select(key => key.Column)).ToList();

    private static object? Read(object entity, string column) =>
        Property(entity.GetType(), column)?.GetValue(entity);

    private static PropertyInfo? Property(Type type, string name) =>
        Properties.GetOrAdd((type, name), key =>
            key.Item1.GetProperty(key.Item2, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase));
}
