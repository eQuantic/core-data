using System.Linq.Expressions;
using System.Reflection;
using eQuantic.Linq.Expressions;

namespace eQuantic.Core.Data.Cassandra;

/// <summary>A clustering-key column and its order.</summary>
/// <param name="Column">The column name.</param>
/// <param name="Descending">Whether the clustering order is descending.</param>
public sealed record CassandraClusteringColumn(string Column, bool Descending);

/// <summary>A table column and its CQL type (used to author <c>CREATE TABLE</c> migrations).</summary>
/// <param name="Name">The column name.</param>
/// <param name="CqlType">The CQL type (e.g. <c>text</c>, <c>uuid</c>, <c>timestamp</c>).</param>
public sealed record CassandraColumn(string Name, string CqlType)
{
    /// <summary>The CLR member the column stores (the column name itself when no rename applies).</summary>
    public string Member { get; init; } = Name;
}

/// <summary>
///     The Cassandra mapping for an entity: its table, the partition key (one or more columns), the clustering
///     keys (ordered) and the full column set. Cassandra only queries efficiently by these keys, so they are
///     declared up front and drive the CQL that the provider generates.
/// </summary>
public abstract class CassandraEntityConfiguration
{
    /// <summary>Initializes the configuration.</summary>
    protected CassandraEntityConfiguration(Type entityType, string tableName, IReadOnlyList<string> partitionKeys,
        IReadOnlyList<CassandraClusteringColumn> clusteringKeys, IReadOnlyList<CassandraColumn> columns, string keyColumn,
        IReadOnlyList<string>? counterColumns = null, IReadOnlyList<CassandraSearchColumn>? searchColumns = null)
    {
        EntityType = entityType;
        TableName = tableName;
        PartitionKeys = partitionKeys;
        ClusteringKeys = clusteringKeys;
        Columns = columns;
        KeyColumn = keyColumn;
        CounterColumns = counterColumns ?? [];
        SearchColumns = searchColumns ?? [];
    }

    /// <summary>The entity type.</summary>
    public Type EntityType { get; }

    /// <summary>The table name.</summary>
    public string TableName { get; }

    /// <summary>The partition key columns (in order).</summary>
    public IReadOnlyList<string> PartitionKeys { get; }

    /// <summary>The clustering key columns (in order).</summary>
    public IReadOnlyList<CassandraClusteringColumn> ClusteringKeys { get; }

    /// <summary>Every column with its CQL type.</summary>
    public IReadOnlyList<CassandraColumn> Columns { get; }

    /// <summary>The column the entity's key maps to (used by point lookups); defaults to the first partition key.</summary>
    public string KeyColumn { get; }

    /// <summary>The counter columns (a counter table mutates them through increments, never inserts).</summary>
    public IReadOnlyList<string> CounterColumns { get; }

    /// <summary>The search-indexed columns — <c>LIKE</c> pushes down on these (a SASI index backs each one).</summary>
    public IReadOnlyList<CassandraSearchColumn> SearchColumns { get; }

    /// <summary>Whether <paramref name="column" /> is a counter column.</summary>
    public bool IsCounter(string column) => CounterColumns.Any(counter => Same(counter, column));

    /// <summary>Whether <paramref name="column" /> is search-indexed, and in which mode.</summary>
    public bool CanLike(string column, out CassandraSearchMode mode)
    {
        var search = SearchColumns.FirstOrDefault(candidate => Same(candidate.Column, column));
        mode = search?.Mode ?? CassandraSearchMode.Prefix;
        return search is not null;
    }

    /// <summary>Whether <paramref name="column" /> is part of the primary key (partition or clustering).</summary>
    public bool IsKey(string column) =>
        PartitionKeys.Any(key => Same(key, column)) || ClusteringKeys.Any(key => Same(key.Column, column));

    /// <summary>Whether <paramref name="column" /> is a clustering key (supports range predicates).</summary>
    public bool IsClusteringKey(string column) => ClusteringKeys.Any(key => Same(key.Column, column));

    /// <summary>The stored column name for a CLR member (the member itself when no column stores it).</summary>
    public string ColumnFor(string member) =>
        Columns.FirstOrDefault(column => Same(column.Member, member))?.Name ?? member;

    /// <summary>The CLR member behind a stored column name (the column itself when no mapping declares it).</summary>
    public string MemberFor(string column) =>
        Columns.FirstOrDefault(candidate => Same(candidate.Name, column))?.Member ?? column;

    internal static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What a SASI search index can match — and therefore which <c>LIKE</c> shapes push down.</summary>
public enum CassandraSearchMode
{
    /// <summary>Prefix matching only (<c>StartsWith</c> / <c>LIKE 'value%'</c>).</summary>
    Prefix,

    /// <summary>Substring matching (<c>StartsWith</c>, <c>EndsWith</c>, <c>Contains</c> and any <c>LIKE</c> pattern).</summary>
    Contains,
}

/// <summary>A search-indexed column and its matching mode.</summary>
/// <param name="Column">The column name.</param>
/// <param name="Mode">The SASI matching mode.</param>
public sealed record CassandraSearchColumn(string Column, CassandraSearchMode Mode);

/// <summary>The typed Cassandra mapping for <typeparamref name="TEntity" />.</summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public sealed class CassandraEntityConfiguration<TEntity> : CassandraEntityConfiguration
    where TEntity : class
{
    internal CassandraEntityConfiguration(string tableName, IReadOnlyList<string> partitionKeys,
        IReadOnlyList<CassandraClusteringColumn> clusteringKeys, IReadOnlyList<CassandraColumn> columns, string keyColumn,
        IReadOnlyList<string>? counterColumns = null, IReadOnlyList<CassandraSearchColumn>? searchColumns = null)
        : base(typeof(TEntity), tableName, partitionKeys, clusteringKeys, columns, keyColumn, counterColumns, searchColumns)
    {
    }
}

/// <summary>The registered Cassandra mappings, keyed by entity type.</summary>
public sealed class CassandraModel
{
    private readonly Dictionary<Type, CassandraEntityConfiguration> _configurations = new();

    /// <summary>The registered configurations.</summary>
    public IReadOnlyDictionary<Type, CassandraEntityConfiguration> Configurations => _configurations;

    /// <summary>Gets the configuration for an entity type, or throws when it was not registered.</summary>
    /// <param name="entityType">The entity type.</param>
    public CassandraEntityConfiguration For(Type entityType) =>
        _configurations.TryGetValue(entityType, out var configuration)
            ? configuration
            : throw new InvalidOperationException(
                $"No Cassandra configuration is registered for '{entityType.Name}'. Register it with Entity<{entityType.Name}>(...).");

    internal void Add(CassandraEntityConfiguration configuration) => _configurations[configuration.EntityType] = configuration;

    /// <summary>
    ///     Describes every mapping decision the model made — table, partition and clustering keys, columns with
    ///     their CQL types and renames, counters and search indexes — the way <c>Explain()</c> describes a query.
    ///     Read this instead of guessing what the CQL ends up naming.
    /// </summary>
    public string Explain()
    {
        var report = new System.Text.StringBuilder();
        foreach (var configuration in _configurations.Values.OrderBy(entry => entry.EntityType.Name))
        {
            report.AppendLine($"{configuration.EntityType.Name} -> table \"{configuration.TableName}\"");
            report.AppendLine($"  partition key: ({string.Join(", ", configuration.PartitionKeys)})");
            if (configuration.ClusteringKeys.Count > 0)
            {
                report.AppendLine("  clustering keys: " + string.Join(", ",
                    configuration.ClusteringKeys.Select(key => $"{key.Column} {(key.Descending ? "DESC" : "ASC")}")));
            }

            report.AppendLine($"  key column: \"{configuration.KeyColumn}\" (point lookups)");
            foreach (var column in configuration.Columns)
            {
                report.AppendLine($"  column: {column.Member} \"{column.Name}\" ({column.CqlType})");
            }

            foreach (var counter in configuration.CounterColumns)
            {
                report.AppendLine($"  counter: \"{counter}\" (mutates through increments, never inserts)");
            }

            foreach (var search in configuration.SearchColumns)
            {
                report.AppendLine($"  search index: \"{search.Column}\" (SASI {search.Mode}; LIKE pushes down)");
            }
        }

        return report.ToString();
    }
}

/// <summary>Fluent builder for the <see cref="CassandraModel" />.</summary>
public sealed class CassandraModelBuilder
{
    private readonly CassandraModel _model = new();

    /// <summary>Maps <typeparamref name="TEntity" /> to a table, partition key and clustering keys.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="configure">The fluent configuration.</param>
    /// <returns>The same builder for chaining.</returns>
    public CassandraModelBuilder Entity<TEntity>(Action<CassandraEntityBuilder<TEntity>> configure) where TEntity : class
    {
        var builder = new CassandraEntityBuilder<TEntity>();
        configure(builder);
        _model.Add(builder.Build());
        return this;
    }

    internal CassandraModel Build() => _model;
}

/// <summary>Fluent configuration for one entity's Cassandra mapping.</summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public sealed class CassandraEntityBuilder<TEntity> where TEntity : class
{
    private readonly List<string> _partitionKeys = [];
    private readonly List<CassandraClusteringColumn> _clusteringKeys = [];
    private readonly List<string> _counterColumns = [];
    private readonly List<CassandraSearchColumn> _searchColumns = [];
    private readonly HashSet<string> _unmapped = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _storedNames = new(StringComparer.OrdinalIgnoreCase);
    private string? _table;
    private string? _keyColumn;

    internal CassandraEntityBuilder()
    {
        // The eQuantic.Core.Data.Modeling annotations seed the builder; fluent calls override them
        // (conventions < annotations < fluent). Annotations outside the Cassandra vocabulary are ignored.
        if (Data.Modeling.EntityAttribute.NameFor(typeof(TEntity)) is { } name)
        {
            _table = name;
        }

        var properties = typeof(TEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var property in properties
                     .Where(candidate => candidate.GetCustomAttribute<Data.Modeling.PartitionKeyAttribute>() is not null)
                     .OrderBy(candidate => candidate.GetCustomAttribute<Data.Modeling.PartitionKeyAttribute>()!.Order))
        {
            _partitionKeys.Add(property.Name);
        }

        foreach (var property in properties
                     .Where(candidate => candidate.GetCustomAttribute<Data.Modeling.ClusteringKeyAttribute>() is not null)
                     .OrderBy(candidate => candidate.GetCustomAttribute<Data.Modeling.ClusteringKeyAttribute>()!.Order))
        {
            _clusteringKeys.Add(new CassandraClusteringColumn(property.Name,
                property.GetCustomAttribute<Data.Modeling.ClusteringKeyAttribute>()!.Descending));
        }

        foreach (var property in properties)
        {
            if (property.GetCustomAttribute<Data.Modeling.UnmappedAttribute>() is not null)
            {
                _unmapped.Add(property.Name);
            }

            if (property.GetCustomAttribute<Data.Modeling.StoredAsAttribute>() is { } stored)
            {
                _storedNames[property.Name] = stored.Name;
            }

            if (property.GetCustomAttribute<Data.Modeling.EntityKeyAttribute>() is not null)
            {
                _keyColumn = property.Name;
            }

            if (property.GetCustomAttribute<Data.Modeling.CounterAttribute>() is not null)
            {
                _counterColumns.Add(property.Name);
            }

            if (property.GetCustomAttribute<Data.Modeling.SearchIndexAttribute>() is { } search)
            {
                _searchColumns.Add(new CassandraSearchColumn(property.Name,
                    search.Mode == Data.Modeling.SearchMode.Prefix ? CassandraSearchMode.Prefix : CassandraSearchMode.Contains));
            }
        }
    }

    /// <summary>Sets the table name (defaults to the entity type name).</summary>
    /// <param name="name">The table name.</param>
    public CassandraEntityBuilder<TEntity> Table(string name)
    {
        _table = name;
        return this;
    }

    /// <summary>
    ///     Sets the member's column name when the convention (the member name itself) does not fit. Every CQL
    ///     the provider generates — DDL, reads, writes, filters — uses the stored name; the entity keeps its own.
    /// </summary>
    /// <typeparam name="TMember">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="columnName">The column name.</param>
    public CassandraEntityBuilder<TEntity> Column<TMember>(Expression<Func<TEntity, TMember>> selector, string columnName)
    {
        _storedNames[selector.GetMemberName()] = columnName;
        return this;
    }

    /// <summary>Adds a partition key column (call more than once for a composite partition key).</summary>
    /// <typeparam name="TKey">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    public CassandraEntityBuilder<TEntity> PartitionKey<TKey>(Expression<Func<TEntity, TKey>> selector)
    {
        _partitionKeys.Add(selector.GetMemberName());
        return this;
    }

    /// <summary>Adds a clustering key column (call in order).</summary>
    /// <typeparam name="TKey">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="descending">Whether the clustering order is descending.</param>
    public CassandraEntityBuilder<TEntity> ClusteringKey<TKey>(Expression<Func<TEntity, TKey>> selector, bool descending = false)
    {
        _clusteringKeys.Add(new CassandraClusteringColumn(selector.GetMemberName(), descending));
        return this;
    }

    /// <summary>Sets the column the entity key maps to for point lookups (defaults to the first partition key).</summary>
    /// <typeparam name="TKey">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    public CassandraEntityBuilder<TEntity> Key<TKey>(Expression<Func<TEntity, TKey>> selector)
    {
        _keyColumn = selector.GetMemberName();
        return this;
    }

    /// <summary>
    ///     Marks a column as a Cassandra <c>counter</c>. A counter table mutates through increments
    ///     (<c>UpdateMany(filter, x =&gt; new E { N = x.N + n })</c>) — never inserts — and every non-key column
    ///     of the table must be a counter.
    /// </summary>
    /// <typeparam name="TKey">The member type (an integral type; stored as <c>counter</c>/bigint).</typeparam>
    /// <param name="selector">The member selector.</param>
    public CassandraEntityBuilder<TEntity> Counter<TKey>(Expression<Func<TEntity, TKey>> selector)
    {
        _counterColumns.Add(selector.GetMemberName());
        return this;
    }

    /// <summary>
    ///     Declares a SASI search index on a text column — <c>StartsWith</c>/<c>EndsWith</c>/<c>Contains</c> and
    ///     <c>Db.Like</c> push down as native <c>LIKE</c> on it (no scan opt-in: the index serves the match).
    ///     The migration runner creates the index with <c>EnsureCollection()</c>. SASI must be enabled on the
    ///     cluster (<c>sasi_indexes_enabled: true</c>).
    /// </summary>
    /// <param name="selector">The member selector.</param>
    /// <param name="mode">
    ///     What the index matches: <see cref="CassandraSearchMode.Contains" /> (substrings — the default) or
    ///     <see cref="CassandraSearchMode.Prefix" /> (cheaper, <c>StartsWith</c> only).
    /// </param>
    public CassandraEntityBuilder<TEntity> SearchIndex(Expression<Func<TEntity, string?>> selector,
        CassandraSearchMode mode = CassandraSearchMode.Contains)
    {
        _searchColumns.Add(new CassandraSearchColumn(selector.GetMemberName(), mode));
        return this;
    }

    internal CassandraEntityConfiguration<TEntity> Build()
    {
        if (_partitionKeys.Count == 0)
        {
            throw new InvalidOperationException(
                $"Entity '{typeof(TEntity).Name}' must declare a partition key (e.g. PartitionKey(x => x.CustomerId)).");
        }

        // The builder collects CLR member names throughout (annotations and fluent calls alike); the stored
        // rename map is applied here, once, so a Column()/[StoredAs] declared after PartitionKey(...) still lands.
        string Stored(string member) => _storedNames.GetValueOrDefault(member, member);

        var columns = typeof(TEntity)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0
                               && !_unmapped.Contains(property.Name))
            .Select(property => new CassandraColumn(Stored(property.Name),
                _counterColumns.Any(counter => CassandraEntityConfiguration.Same(counter, property.Name))
                    ? "counter"
                    : CassandraTypes.Cql(property.PropertyType)) { Member = property.Name })
            .ToList();

        if (_counterColumns.Count > 0)
        {
            var keys = _partitionKeys.Concat(_clusteringKeys.Select(key => key.Column)).ToList();
            if (_counterColumns.Any(counter => keys.Any(key => CassandraEntityConfiguration.Same(key, counter))))
            {
                throw new InvalidOperationException(
                    $"Entity '{typeof(TEntity).Name}': a primary key column cannot be a counter.");
            }

            var invalid = columns.Where(column =>
                    !keys.Any(key => CassandraEntityConfiguration.Same(key, column.Member)) && column.CqlType != "counter")
                .Select(column => column.Member)
                .ToList();
            if (invalid.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Entity '{typeof(TEntity).Name}': Cassandra requires every non-key column of a counter table to be a counter; " +
                    $"'{string.Join("', '", invalid)}' is not.");
            }
        }

        return new CassandraEntityConfiguration<TEntity>(
            _table ?? typeof(TEntity).Name,
            _partitionKeys.Select(Stored).ToList(),
            _clusteringKeys.Select(key => key with { Column = Stored(key.Column) }).ToList(),
            columns,
            Stored(_keyColumn ?? _partitionKeys[0]),
            _counterColumns.Select(Stored).ToList(),
            _searchColumns.Select(search => search with { Column = Stored(search.Column) }).ToList());
    }
}

/// <summary>Maps CLR types to their CQL counterparts for <c>CREATE TABLE</c> authoring.</summary>
internal static class CassandraTypes
{
    public static string Cql(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying.IsGenericType)
        {
            var definition = underlying.GetGenericTypeDefinition();
            var arguments = underlying.GetGenericArguments();
            if (definition == typeof(List<>) || definition == typeof(IList<>) || definition == typeof(IReadOnlyList<>)
                || definition == typeof(ICollection<>) || definition == typeof(IEnumerable<>))
            {
                return $"list<{Cql(arguments[0])}>";
            }

            if (definition == typeof(HashSet<>) || definition == typeof(ISet<>))
            {
                return $"set<{Cql(arguments[0])}>";
            }

            if (definition == typeof(Dictionary<,>) || definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
            {
                return $"map<{Cql(arguments[0])}, {Cql(arguments[1])}>";
            }
        }

        return underlying switch
        {
            _ when underlying == typeof(string) => "text",
            _ when underlying == typeof(Guid) => "uuid",
            _ when underlying == typeof(bool) => "boolean",
            _ when underlying == typeof(byte) => "tinyint",
            _ when underlying == typeof(short) => "smallint",
            _ when underlying == typeof(int) => "int",
            _ when underlying == typeof(long) => "bigint",
            _ when underlying == typeof(float) => "float",
            _ when underlying == typeof(double) => "double",
            _ when underlying == typeof(decimal) => "decimal",
            _ when underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset) => "timestamp",
            _ when underlying == typeof(TimeSpan) => "time",
            _ when underlying == typeof(byte[]) => "blob",
            _ => throw new NotSupportedException($"No CQL type mapping for '{underlying.Name}'."),
        };
    }
}
