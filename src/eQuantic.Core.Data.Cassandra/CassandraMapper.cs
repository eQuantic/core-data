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

    /// <summary>
    ///     Builds an upsert (<c>INSERT</c>) for the row, leaving out the store-generated columns: an <c>INSERT</c> only
    ///     writes the columns it names, so a row that exists keeps the values the store assigned.
    /// </summary>
    public static (string Cql, object?[] Values) BuildUpsert(CassandraEntityConfiguration configuration, object entity)
    {
        var columns = configuration.Columns.Where(column => !configuration.IsStoreGenerated(column.Name)).ToList();
        var names = string.Join(", ", columns.Select(column => column.Name));
        var placeholders = string.Join(", ", columns.Select(_ => "?"));
        var values = columns.Select(column => Stored(entity, column)).ToArray();
        return ($"INSERT INTO {configuration.TableName} ({names}) VALUES ({placeholders})", values);
    }

    /// <summary>
    ///     Builds the <c>INSERT</c> of a new row with what the store assigns: the clock columns take
    ///     <c>toTimestamp(now())</c>, evaluated by the coordinator, and the sequence columns the numbers their
    ///     sequences handed out (already on the entity). Without store-generated columns it is <see cref="BuildUpsert" />.
    /// </summary>
    public static (string Cql, object?[] Values) BuildInsert(CassandraEntityConfiguration configuration, object entity)
    {
        var names = string.Join(", ", configuration.Columns.Select(column => column.Name));
        var placeholders = string.Join(", ", configuration.Columns.Select(column =>
            configuration.StoreGenerated.Any(generated => generated.Value == Data.Modeling.StoreValue.Clock
                                                          && CassandraEntityConfiguration.Same(generated.Column, column.Name))
                ? "toTimestamp(now())"
                : "?"));
        var values = configuration.Columns
            .Where(column => !configuration.StoreGenerated.Any(generated => generated.Value == Data.Modeling.StoreValue.Clock
                                                                            && CassandraEntityConfiguration.Same(generated.Column, column.Name)))
            .Select(column => Stored(entity, column))
            .ToArray();
        return ($"INSERT INTO {configuration.TableName} ({names}) VALUES ({placeholders})", values);
    }

    /// <summary>
    ///     The store-generated columns an insert has to read back: the clock columns, which the coordinator fills
    ///     (<c>toTimestamp(now())</c>). A sequence's number is on the entity before the insert is built, so it is never read.
    /// </summary>
    public static IReadOnlyList<CassandraGeneratedColumn> ReadBackColumns(CassandraEntityConfiguration configuration) =>
        configuration.StoreGenerated.Where(generated => generated.Value == Data.Modeling.StoreValue.Clock).ToList();

    /// <summary>
    ///     Builds the read of a row's clock columns by its primary key; <c>null</c> when the entity has none, and nothing is
    ///     read back.
    /// </summary>
    public static (string Cql, object?[] Values)? BuildReadBack(CassandraEntityConfiguration configuration, object entity)
    {
        var generated = ReadBackColumns(configuration);
        if (generated.Count == 0)
        {
            return null;
        }

        var keys = PrimaryKey(configuration);
        var columns = string.Join(", ", generated.Select(column => column.Column));
        var where = string.Join(" AND ", keys.Select(key => $"{key} = ?"));
        var values = keys.Select(key => Stored(entity, configuration.Column(key))).ToArray();
        return ($"SELECT {columns} FROM {configuration.TableName} WHERE {where}", values);
    }

    /// <summary>Sets a read-back row's clock values on the entity; a value the read did not see fails loudly.</summary>
    public static void AssignReadBack(CassandraEntityConfiguration configuration, object entity, Row? row)
    {
        foreach (var generated in ReadBackColumns(configuration))
        {
            var column = configuration.Column(generated.Column)!;
            var property = Property(entity.GetType(), generated.Member)!;
            var name = generated.Column.ToLowerInvariant();
            if (row is null || row.IsNull(name))
            {
                throw new InvalidOperationException(
                    $"The store-generated '{entity.GetType().Name}.{generated.Member}' could not be read back by the row's key: " +
                    "the read did not see the insert. Commit at a consistency whose reads see its writes (QUORUM) when " +
                    "the keyspace keeps several replicas.");
            }

            property.SetValue(entity, Value(row, column, property.PropertyType, name));
        }
    }

    /// <summary>Builds the read of the entity's row, every column, by its full primary key.</summary>
    public static (string Cql, object?[] Values) BuildRead(CassandraEntityConfiguration configuration, object entity)
    {
        var keys = PrimaryKey(configuration);
        var where = string.Join(" AND ", keys.Select(key => $"{key} = ?"));
        var values = keys.Select(key => Stored(entity, configuration.Column(key))).ToArray();
        return ($"SELECT * FROM {configuration.TableName} WHERE {where}", values);
    }

    /// <summary>The partition an entity's row lives in, as text: its partition key's stored values.</summary>
    public static string PartitionText(CassandraEntityConfiguration configuration, object entity) =>
        PartitionText(configuration.PartitionKeys.Select(key => Stored(entity, configuration.Column(key))));

    /// <summary>The partition a row lives in, as text: its partition key's values.</summary>
    public static string PartitionText(CassandraEntityConfiguration configuration, Row row) =>
        PartitionText(configuration.PartitionKeys.Select(key => row.GetValue<object>(key.ToLowerInvariant())));

    private static string PartitionText(IEnumerable<object?> values) =>
        string.Join("\u001f", values.Select(value => value switch
        {
            null => string.Empty,
            IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        }));

    /// <summary>
    ///     Whether a row read after an insert timed out is the entity's: every column the insert wrote from the entity (all
    ///     but the clock the coordinator filled) holds its value.
    /// </summary>
    public static bool HoldsInsert(CassandraEntityConfiguration configuration, object stored, object entity) =>
        configuration.Columns
            .Where(column => !configuration.StoreGenerated.Any(generated => generated.Value == Data.Modeling.StoreValue.Clock
                                                                            && CassandraEntityConfiguration.Same(generated.Column, column.Name)))
            .All(column => Same(Read(stored, column.Member), Read(entity, column.Member)));

    /// <summary>Equality of two member values as Cassandra stores them: timestamps to the millisecond, collections by their items.</summary>
    public static bool Same(object? left, object? right) => (left, right) switch
    {
        (null, null) => true,
        (DateTime a, DateTime b) => Math.Abs((a.ToUniversalTime() - b.ToUniversalTime()).TotalMilliseconds) < 1,
        (DateTimeOffset a, DateTimeOffset b) => Math.Abs((a - b).TotalMilliseconds) < 1,
        (System.Collections.IEnumerable a, System.Collections.IEnumerable b) when left is not string =>
            a.Cast<object?>().SequenceEqual(b.Cast<object?>()),
        _ => Equals(left, right),
    };

    /// <summary>Builds a <c>DELETE</c> targeting the row's full primary key.</summary>
    public static (string Cql, object?[] Values) BuildDelete(CassandraEntityConfiguration configuration, object entity)
    {
        var keys = PrimaryKey(configuration);
        var where = string.Join(" AND ", keys.Select(key => $"{key} = ?"));
        var values = keys.Select(key => Stored(entity, configuration.Column(key))).ToArray();
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
            var (insert, values) = BuildInsert(configuration, entity);
            return (insert + " IF NOT EXISTS", values);
        }

        property.SetValue(entity, Convert.ChangeType(current + 1,
            Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType));

        var keys = PrimaryKey(configuration);
        var sets = configuration.Columns
            .Where(column => !keys.Any(key => CassandraEntityConfiguration.Same(key, column.Name)))
            .ToList();
        // An update never sets a store-generated column: the row keeps what the store assigned.
        sets = sets.Where(column => !configuration.IsStoreGenerated(column.Name)).ToList();
        var assignments = string.Join(", ", sets.Select(column => $"{column.Name} = ?"));
        var where = string.Join(" AND ", keys.Select(key => $"{key} = ?"));
        object?[] parameters =
        [
            .. sets.Select(column => Stored(entity, column)),
            .. keys.Select(key => Stored(entity, configuration.Column(key))),
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

            property.SetValue(entity, Value(row, column, property.PropertyType, name));
        }

        return entity;
    }

    /// <summary>Reads a column in its stored form and gives it back as the member's value.</summary>
    private static object? Value(Row row, CassandraColumn column, Type memberType, string name) =>
        CassandraValues.FromStored(memberType, row.GetValue(CassandraValues.StoredType(column, memberType), name));

    /// <summary>Reads a member from the entity in the form its column stores.</summary>
    private static object? Stored(object entity, CassandraColumn? column) =>
        column is null ? null : CassandraValues.ToStored(column, Read(entity, column.Member));

    /// <summary>The primary key columns (partition key then clustering keys), in order.</summary>
    public static IReadOnlyList<string> PrimaryKey(CassandraEntityConfiguration configuration) =>
        configuration.PartitionKeys.Concat(configuration.ClusteringKeys.Select(key => key.Column)).ToList();

    private static object? Read(object entity, string column) =>
        Property(entity.GetType(), column)?.GetValue(entity);

    private static PropertyInfo? Property(Type type, string name) =>
        Properties.GetOrAdd((type, name), key =>
            key.Item1.GetProperty(key.Item2, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase));
}
