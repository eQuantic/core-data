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
        var values = columns.Select(column => Read(entity, column.Member)).ToArray();
        return ($"INSERT INTO {configuration.TableName} ({names}) VALUES ({placeholders})", values);
    }

    /// <summary>Builds a <c>DELETE</c> targeting the row's full primary key.</summary>
    public static (string Cql, object?[] Values) BuildDelete(CassandraEntityConfiguration configuration, object entity)
    {
        var keys = PrimaryKey(configuration);
        var where = string.Join(" AND ", keys.Select(key => $"{key} = ?"));
        var values = keys.Select(key => Read(entity, configuration.MemberFor(key))).ToArray();
        return ($"DELETE FROM {configuration.TableName} WHERE {where}", values);
    }

    /// <summary>
    ///     Builds the lightweight-transaction write for a concurrency-token entity, bumping the token on the
    ///     entity: a version at its default (0) means a new row (<c>INSERT … IF NOT EXISTS</c>, version written as
    ///     1); anything else means a conditional update (<c>UPDATE … SET non-keys WHERE keys IF token = old</c>).
    ///     The caller must check the result's <c>[applied]</c> cell.
    /// </summary>
    public static (string Cql, object?[] Values) BuildConditionalUpsert(CassandraEntityConfiguration configuration, object entity)
    {
        var token = configuration.ConcurrencyColumn!;
        var member = configuration.MemberFor(token);
        var property = Property(entity.GetType(), member)
                       ?? throw new InvalidOperationException(
                           $"'{entity.GetType().Name}' has no member '{member}' backing the concurrency token.");
        var current = Convert.ToInt64(property.GetValue(entity) ?? 0L);

        if (current == 0)
        {
            property.SetValue(entity, Convert.ChangeType(1L, Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType));
            var (insert, values) = BuildUpsert(configuration, entity);
            return (insert + " IF NOT EXISTS", values);
        }

        property.SetValue(entity, Convert.ChangeType(current + 1,
            Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType));

        var keys = PrimaryKey(configuration);
        var sets = configuration.Columns
            .Where(column => !keys.Any(key => CassandraEntityConfiguration.Same(key, column.Name)))
            .ToList();
        var assignments = string.Join(", ", sets.Select(column => $"{column.Name} = ?"));
        var where = string.Join(" AND ", keys.Select(key => $"{key} = ?"));
        object?[] parameters =
        [
            .. sets.Select(column => Read(entity, column.Member)),
            .. keys.Select(key => Read(entity, configuration.MemberFor(key))),
            current,
        ];
        return ($"UPDATE {configuration.TableName} SET {assignments} WHERE {where} IF {token} = ?", parameters);
    }

    /// <summary>Builds the conditional <c>DELETE … IF token = current</c> for a concurrency-token entity.</summary>
    public static (string Cql, object?[] Values) BuildConditionalDelete(CassandraEntityConfiguration configuration, object entity)
    {
        var token = configuration.ConcurrencyColumn!;
        var current = Convert.ToInt64(Read(entity, configuration.MemberFor(token)) ?? 0L);
        var (cql, values) = BuildDelete(configuration, entity);
        return current == 0 ? (cql, values) : ($"{cql} IF {token} = ?", [.. values, current]);
    }

    /// <summary>Materializes a row into a new <typeparamref name="TEntity" />.</summary>
    public static TEntity Materialize<TEntity>(CassandraEntityConfiguration configuration, Row row) =>
        Materialize<TEntity>(configuration, row, null);

    /// <summary>
    ///     Materializes a row into a new <typeparamref name="TEntity" />, reading only the columns in
    ///     <paramref name="only" /> (a projected <c>SELECT</c> exposes just those; the rest stay default).
    /// </summary>
    public static TEntity Materialize<TEntity>(CassandraEntityConfiguration configuration, Row row, IReadOnlySet<string>? only)
    {
        var entity = Activator.CreateInstance<TEntity>()!;
        foreach (var column in configuration.Columns)
        {
            if (only is not null && !only.Contains(column.Name))
            {
                continue;
            }

            var property = Property(typeof(TEntity), column.Member);

            // Cassandra folds unquoted identifiers to lower case, so a row exposes each column under its
            // lower-cased name; the driver's by-name lookup is case-sensitive, so read with that name (the
            // property, matched case-insensitively, still carries the original casing).
            var name = column.Name.ToLowerInvariant();
            if (property is null || !property.CanWrite || row.IsNull(name))
            {
                continue;
            }

            property.SetValue(entity, row.GetValue(property.PropertyType, name));
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
