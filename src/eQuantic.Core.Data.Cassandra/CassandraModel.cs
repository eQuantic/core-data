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

    /// <summary>
    ///     The member's CLR type, which says how a value is stored: an enum as its label (a <c>text</c> column) or its
    ///     number, a JSON member as its JSON text, anything else as itself.
    /// </summary>
    public Type ClrType { get; init; } = typeof(object);
}

/// <summary>A column whose value the store assigns on insert.</summary>
/// <param name="Column">The stored column name.</param>
/// <param name="Member">The CLR member.</param>
/// <param name="Value">What the store assigns (<see cref="Data.Modeling.StoreValue.Sequence" /> or <see cref="Data.Modeling.StoreValue.Clock" />).</param>
public sealed record CassandraGeneratedColumn(string Column, string Member, Data.Modeling.StoreValue Value);

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
        IReadOnlyList<string>? counterColumns = null, IReadOnlyList<CassandraSearchColumn>? searchColumns = null,
        string? concurrencyColumn = null, int? defaultTtlSeconds = null, IReadOnlyList<CassandraGeneratedColumn>? storeGenerated = null)
    {
        EntityType = entityType;
        StoreGenerated = storeGenerated ?? [];
        TableName = tableName;
        PartitionKeys = partitionKeys;
        ClusteringKeys = clusteringKeys;
        Columns = columns;
        KeyColumn = keyColumn;
        CounterColumns = counterColumns ?? [];
        SearchColumns = searchColumns ?? [];
        ConcurrencyColumn = concurrencyColumn;
        DefaultTtlSeconds = defaultTtlSeconds;
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
    public IReadOnlyList<CassandraSearchColumn> SearchColumns { get; private set; }

    /// <summary>The same mapping with other search columns (what the cluster's indexes really match).</summary>
    /// <param name="searchColumns">The search columns.</param>
    internal CassandraEntityConfiguration WithSearchColumns(IReadOnlyList<CassandraSearchColumn> searchColumns)
    {
        var copy = (CassandraEntityConfiguration)MemberwiseClone();
        copy.SearchColumns = searchColumns;
        return copy;
    }

    /// <summary>
    ///     The optimistic-concurrency column (a version counter the entity carries), or <c>null</c>. Writes on a
    ///     token entity become lightweight transactions: <c>INSERT … IF NOT EXISTS</c> for a new row,
    ///     <c>UPDATE … IF version = old</c> (bumping the version) otherwise — Paxos per write, the explicit cost
    ///     of the declaration. An unapplied write throws <c>ConcurrencyConflictException</c>.
    /// </summary>
    public string? ConcurrencyColumn { get; }

    /// <summary>The table's <c>default_time_to_live</c> in seconds, or <c>null</c> (rows never expire by default).</summary>
    public int? DefaultTtlSeconds { get; }

    /// <summary>
    ///     The columns the store assigns on insert (<c>[StoreGenerated]</c>, or <c>Generated(...)</c>): a number of a
    ///     sequence (a lightweight-transaction compare-and-set on <c>equantic_sequences</c>) or the store's clock
    ///     (<c>toTimestamp(now())</c> in the <c>INSERT</c>), read back by the row's key; an update never sets them.
    /// </summary>
    public IReadOnlyList<CassandraGeneratedColumn> StoreGenerated { get; }

    /// <summary>Whether <paramref name="column" /> is assigned by the store on insert.</summary>
    public bool IsStoreGenerated(string column) => StoreGenerated.Any(generated => Same(generated.Column, column));

    /// <summary>The column mapping a stored column name, or <c>null</c>.</summary>
    public CassandraColumn? Column(string column) => Columns.FirstOrDefault(candidate => Same(candidate.Name, column));
    /// <summary>Whether <paramref name="column" /> is a counter column.</summary>
    public bool IsCounter(string column) => CounterColumns.Any(counter => Same(counter, column));

    /// <summary>Whether <paramref name="column" /> is search-indexed, and in which mode.</summary>
    public bool CanLike(string column, out CassandraSearchMode mode)
    {
        var search = SearchColumns.FirstOrDefault(candidate => Same(candidate.Column, column));
        mode = search?.Mode ?? CassandraSearchMode.Prefix;
        return search is not null;
    }

    /// <summary>Whether <paramref name="column" /> has a search index that matches without regard to case, and in which mode.</summary>
    public bool CanLikeIgnoringCase(string column, out CassandraSearchMode mode)
    {
        var search = SearchColumns.FirstOrDefault(candidate => Same(candidate.Column, column) && candidate.CaseInsensitive);
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
public sealed record CassandraSearchColumn(string Column, CassandraSearchMode Mode)
{
    /// <summary>
    ///     Whether the index matches without regard to case: SASI's non-tokenizing analyzer with <c>case_sensitive: false</c>,
    ///     on which the text tests that ignore case push down as <c>LIKE</c>.
    /// </summary>
    public bool CaseInsensitive { get; init; }
}

/// <summary>The typed Cassandra mapping for <typeparamref name="TEntity" />.</summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public sealed class CassandraEntityConfiguration<TEntity> : CassandraEntityConfiguration
    where TEntity : class
{
    internal CassandraEntityConfiguration(string tableName, IReadOnlyList<string> partitionKeys,
        IReadOnlyList<CassandraClusteringColumn> clusteringKeys, IReadOnlyList<CassandraColumn> columns, string keyColumn,
        IReadOnlyList<string>? counterColumns = null, IReadOnlyList<CassandraSearchColumn>? searchColumns = null,
        string? concurrencyColumn = null, int? defaultTtlSeconds = null, IReadOnlyList<CassandraGeneratedColumn>? storeGenerated = null)
        : base(typeof(TEntity), tableName, partitionKeys, clusteringKeys, columns, keyColumn, counterColumns, searchColumns,
            concurrencyColumn, defaultTtlSeconds, storeGenerated)
    {
    }
}

/// <summary>The registered Cassandra mappings, keyed by entity type.</summary>
public sealed class CassandraModel
{
    private readonly Dictionary<Type, CassandraEntityConfiguration> _configurations = new();
    private readonly HashSet<Type> _labelEnums = [];

    /// <summary>The enums the model declared stored as their labels (<c>Enum&lt;TEnum&gt;()</c>).</summary>
    public IReadOnlyCollection<Type> LabelEnums => _labelEnums;

    internal HashSet<Type> DeclaredLabelEnums => _labelEnums;

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

        // The enums stored as labels: those the model declared, and the annotated ones its tables hold.
        foreach (var enumType in _configurations.Values
                     .SelectMany(configuration => configuration.Columns)
                     .Where(column => column.CqlType == "text")
                     .Select(column => Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType)
                     .Where(type => type.IsEnum)
                     .Concat(_labelEnums)
                     .Distinct()
                     .OrderBy(type => type.Name))
        {
            report.AppendLine($"{enumType.Name} -> stored as labels, text (" +
                              string.Join(", ", Data.Modeling.EnumLabels.Of(enumType).Select(label =>
                                  label.Key == label.Value ? label.Key : $"{label.Key} '{label.Value}'")) + ")");
        }

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
            if (configuration.ConcurrencyColumn is { } token)
            {
                report.AppendLine($"  concurrency token: \"{token}\" (writes are LWTs: IF version = old, Paxos per write)");
            }

            if (configuration.DefaultTtlSeconds is { } ttl)
            {
                report.AppendLine($"  default TTL: {ttl}s (table default_time_to_live; writes can override)");
            }

            foreach (var column in configuration.Columns)
            {
                var type = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;
                var stored = type.IsEnum
                    ? column.CqlType == "text" ? " labels" : " the enum's number"
                    : Data.Modeling.JsonMembers.Is(type) ? " JSON text" : string.Empty;
                var generated = configuration.StoreGenerated.FirstOrDefault(candidate => CassandraEntityConfiguration.Same(candidate.Column, column.Name)) switch
                {
                    { Value: Data.Modeling.StoreValue.Sequence } =>
                        $" store-generated (sequence: lightweight-transaction compare-and-set on \"{CassandraSequences.TableName}\" row " +
                        $"\"{CassandraSequences.SequenceName(configuration.TableName, column.Name)}\")",
                    { Value: Data.Modeling.StoreValue.Clock } => " store-generated (clock: toTimestamp(now()) in the INSERT, read back by the row's key)",
                    _ => string.Empty,
                };
                report.AppendLine($"  column: {column.Member} \"{column.Name}\" ({column.CqlType}{stored}){generated}");
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
        var builder = new CassandraEntityBuilder<TEntity>(_model.DeclaredLabelEnums);
        configure(builder);
        _model.Add(builder.Build());

        // Built again with the model, so an Enum<TEnum>() declared after the entity still lands on its columns.
        _builds.Add(builder.Build);
        return this;
    }

    private readonly List<Func<CassandraEntityConfiguration>> _builds = [];

    /// <summary>
    ///     Declares <typeparamref name="TEnum" /> stored as its labels, as <see cref="Data.Modeling.EnumTypeAttribute" />
    ///     does on the enum: each member's column is <c>text</c>, written, read, filtered and set-based-updated as its
    ///     <c>[StoredAs]</c> label (or its own name). An enum declared neither way is stored as its number (the CQL
    ///     type of its underlying integral type).
    /// </summary>
    /// <typeparam name="TEnum">The enum.</typeparam>
    /// <returns>The same builder for chaining.</returns>
    public CassandraModelBuilder Enum<TEnum>() where TEnum : struct, System.Enum
    {
        _model.DeclaredLabelEnums.Add(typeof(TEnum));
        return this;
    }

    internal CassandraModel Build()
    {
        foreach (var build in _builds)
        {
            _model.Add(build());
        }

        return _model;
    }
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
    private string? _concurrencyMember;
    private int? _ttlSeconds;
    private readonly IReadOnlySet<Type> _labelEnums;
    private readonly Dictionary<string, Data.Modeling.StoreValue> _generated = new(StringComparer.OrdinalIgnoreCase);

    internal CassandraEntityBuilder(IReadOnlySet<Type> labelEnums)
    {
        _labelEnums = labelEnums;

        // The eQuantic.Core.Data.Modeling annotations seed the builder; fluent calls override them
        // (conventions < annotations < fluent). Annotations outside the Cassandra vocabulary are ignored.
        if (Data.Modeling.EntityAttribute.NameFor(typeof(TEntity)) is { } name)
        {
            _table = name;
        }

        if (System.Attribute.GetCustomAttribute(typeof(TEntity), typeof(Data.Modeling.TimeToLiveAttribute))
            is Data.Modeling.TimeToLiveAttribute timeToLive)
        {
            _ttlSeconds = timeToLive.Seconds;
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

            if (property.GetCustomAttribute<Data.Modeling.ConcurrencyTokenAttribute>() is not null)
            {
                _concurrencyMember = property.Name;
            }

            if (property.GetCustomAttribute<Data.Modeling.StoreGeneratedAttribute>() is { } generated)
            {
                _generated[property.Name] = generated.Value;
            }

            if (property.GetCustomAttribute<Data.Modeling.SearchIndexAttribute>() is { } search)
            {
                _searchColumns.Add(new CassandraSearchColumn(property.Name,
                    search.Mode == Data.Modeling.SearchMode.Prefix ? CassandraSearchMode.Prefix : CassandraSearchMode.Contains)
                {
                    CaseInsensitive = search.CaseInsensitive,
                });
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
    ///     Declares the optimistic-concurrency member: an integral version the entity carries. Writes become
    ///     lightweight transactions — <c>INSERT … IF NOT EXISTS</c> for a new row (version at default),
    ///     <c>UPDATE … IF version = old</c> with a bump otherwise, <c>DELETE … IF version = old</c> on remove —
    ///     and an unapplied write throws <c>ConcurrencyConflictException</c>. The explicit cost: every write on
    ///     the entity pays a Paxos round; entities without a token keep plain (last-write-wins) upserts.
    /// </summary>
    /// <typeparam name="TMember">The version member type (an integral type; stored as bigint).</typeparam>
    /// <param name="selector">The member selector (e.g. <c>x =&gt; x.Version</c>).</param>
    public CassandraEntityBuilder<TEntity> ConcurrencyToken<TMember>(Expression<Func<TEntity, TMember>> selector)
    {
        _concurrencyMember = selector.GetMemberName();
        return this;
    }

    /// <summary>
    ///     Sets the table's <c>default_time_to_live</c>: rows expire that long after their last write unless the
    ///     write overrides it (<c>CommitAsync(o =&gt; o.WithTtl(...))</c>). Applied by the migration's
    ///     <c>EnsureCollection()</c>.
    /// </summary>
    /// <param name="timeToLive">The default time-to-live.</param>
    public CassandraEntityBuilder<TEntity> TimeToLive(TimeSpan timeToLive)
    {
        _ttlSeconds = (int)timeToLive.TotalSeconds;
        return this;
    }

    /// <summary>
    ///     Declares a column outside the primary key whose value the store assigns on insert, as
    ///     <see cref="Data.Modeling.StoreGeneratedAttribute" /> does: the next number of a sequence for an integral
    ///     member (a lightweight-transaction compare-and-set on the <c>equantic_sequences</c> table, created on first
    ///     use, which never hands out a number twice), the store's clock for a date (<c>toTimestamp(now())</c> in the
    ///     <c>INSERT</c>), or the kind given. An <c>Add</c> reads the values back by the row's key, and a
    ///     <c>Modify</c> never sets the column (its <c>INSERT</c> leaves it out, so the stored value stays).
    /// </summary>
    /// <typeparam name="TMember">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="value">What the store assigns; inferred from the member's type by default.</param>
    public CassandraEntityBuilder<TEntity> Generated<TMember>(Expression<Func<TEntity, TMember>> selector,
        Data.Modeling.StoreValue value = Data.Modeling.StoreValue.Inferred)
    {
        _generated[Data.Modeling.StoreGeneratedAttribute.OwnMember(selector.GetMemberPath(), typeof(TEntity))] = value;
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

    /// <summary>
    ///     Declares a SASI search index on a text column, matching with or without regard to case. A case-insensitive
    ///     index (a non-tokenizing analyzer with <c>case_sensitive: false</c>) also serves the text tests that ignore case —
    ///     <c>string.Equals(x.Name, v, StringComparison.OrdinalIgnoreCase)</c>, <c>Contains</c>/<c>StartsWith</c>/<c>EndsWith</c>
    ///     with that comparison, and the same over <c>ToLower()</c>/<c>ToUpper()</c> — as a native <c>LIKE</c>.
    /// </summary>
    /// <param name="selector">The member selector.</param>
    /// <param name="mode">What the index matches: substrings (<see cref="CassandraSearchMode.Contains" />) or prefixes.</param>
    /// <param name="caseInsensitive">Whether the index matches without regard to case.</param>
    public CassandraEntityBuilder<TEntity> SearchIndex(Expression<Func<TEntity, string?>> selector, CassandraSearchMode mode,
        bool caseInsensitive)
    {
        _searchColumns.Add(new CassandraSearchColumn(selector.GetMemberName(), mode) { CaseInsensitive = caseInsensitive });
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
                               && !_unmapped.Contains(property.Name)
                               && !IsNavigation(property.PropertyType))
            .Select(property => new CassandraColumn(Stored(property.Name),
                _counterColumns.Any(counter => CassandraEntityConfiguration.Same(counter, property.Name))
                    ? "counter"
                    : CassandraTypes.Cql(property.PropertyType, IsLabelEnum)) { Member = property.Name, ClrType = property.PropertyType })
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

        if (_concurrencyMember is { } concurrency)
        {
            if (_counterColumns.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Entity '{typeof(TEntity).Name}': a counter table cannot carry a concurrency token (counters only move by increments).");
            }

            var keys = _partitionKeys.Concat(_clusteringKeys.Select(key => key.Column));
            if (keys.Any(key => CassandraEntityConfiguration.Same(key, concurrency)))
            {
                throw new InvalidOperationException(
                    $"Entity '{typeof(TEntity).Name}': the concurrency token cannot be a primary key column ('{concurrency}').");
            }
        }

        var generated = new List<CassandraGeneratedColumn>(_generated.Count);
        foreach (var (member, value) in _generated)
        {
            var column = columns.FirstOrDefault(candidate => CassandraEntityConfiguration.Same(candidate.Member, member))
                         ?? throw new InvalidOperationException(
                             $"Entity '{typeof(TEntity).Name}' has no mapped member '{member}' to declare store-generated.");
            var resolved = Data.Modeling.StoreGeneratedAttribute.Resolve(value, column.ClrType, $"{typeof(TEntity).Name}.{column.Member}");
            var keys = _partitionKeys.Concat(_clusteringKeys.Select(key => key.Column));
            if (keys.Any(key => CassandraEntityConfiguration.Same(key, member))
                || _counterColumns.Any(counter => CassandraEntityConfiguration.Same(counter, member))
                || (_concurrencyMember is not null && CassandraEntityConfiguration.Same(_concurrencyMember, member)))
            {
                throw new InvalidOperationException(
                    $"'{typeof(TEntity).Name}.{column.Member}' is a primary key column, a counter or the concurrency token, and " +
                    "cannot also be declared store-generated: the store reads it back by the row's key, which the client gives.");
            }

            generated.Add(new CassandraGeneratedColumn(column.Name, column.Member, resolved));
        }

        return new CassandraEntityConfiguration<TEntity>(
            _table ?? typeof(TEntity).Name,
            _partitionKeys.Select(Stored).ToList(),
            _clusteringKeys.Select(key => key with { Column = Stored(key.Column) }).ToList(),
            columns,
            Stored(_keyColumn ?? _partitionKeys[0]),
            _counterColumns.Select(Stored).ToList(),
            _searchColumns.Select(search => search with { Column = Stored(search.Column) }).ToList(),
            _concurrencyMember is null ? null : Stored(_concurrencyMember),
            _ttlSeconds,
            generated);
    }

    /// <summary>
    ///     Whether a member is a navigation — another entity, or a collection of them — which a row never stores: it is
    ///     loaded by <c>Include</c> from the related table, and counted there.
    /// </summary>
    private static bool IsNavigation(Type type)
    {
        var element = type == typeof(string) || type == typeof(byte[])
            ? null
            : type.IsArray
                ? type.GetElementType()
                : type.GetInterfaces().Append(type)
                    .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    ?.GetGenericArguments()[0];
        return typeof(Data.Repository.IEntity).IsAssignableFrom(element ?? type);
    }

    /// <summary>Whether an enum is stored as its labels: declared on the model, or annotated <c>[EnumType]</c>.</summary>
    private bool IsLabelEnum(Type enumType) =>
        _labelEnums.Contains(enumType) || Data.Modeling.EnumTypeAttribute.For(enumType) is not null;
}

/// <summary>Maps CLR types to their CQL counterparts for <c>CREATE TABLE</c> authoring.</summary>
internal static class CassandraTypes
{
    /// <summary>
    ///     The CQL type of a member: an enum stored as its labels is <c>text</c>, any other enum the CQL type of its
    ///     number, and a JSON member (<c>JsonDocument</c>, <c>JsonElement</c>, <c>JsonNode</c>) <c>text</c> holding its
    ///     JSON; the rest as <see cref="Cql(Type)" /> maps it.
    /// </summary>
    public static string Cql(Type type, Func<Type, bool> isLabelEnum)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying.IsEnum)
        {
            return isLabelEnum(underlying) ? "text" : Cql(Enum.GetUnderlyingType(underlying));
        }

        return Data.Modeling.JsonMembers.Is(underlying) ? "text" : Cql(type);
    }

    public static string Cql(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying.IsEnum)
        {
            throw new NotSupportedException(
                $"No CQL type mapping for a collection of '{underlying.Name}': an enum is mapped as a member of its own, " +
                "not as the element of a collection.");
        }

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
