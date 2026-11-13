using System.Linq.Expressions;
using System.Reflection;
using eQuantic.Linq.Expressions;

namespace eQuantic.Core.Data.Relational;

/// <summary>A member's value converter: how a domain type (a Value Object, an enum-as-string) maps to its stored scalar.</summary>
/// <param name="StoredType">The stored CLR type (drives the DDL column type and parameter binding).</param>
/// <param name="ToStored">Converts the member value into the stored value.</param>
/// <param name="FromStored">Converts the stored value back into the member value.</param>
public sealed record RelationalConverter(Type StoredType, Func<object?, object?> ToStored, Func<object?, object?> FromStored);

/// <summary>A declared navigation: the member, the foreign-key member and which side holds it.</summary>
/// <param name="Member">The navigation property name.</param>
/// <param name="ForeignKey">The foreign-key member: on <b>this</b> entity for a reference, on the <b>element</b> for a collection.</param>
/// <param name="Collection">Whether the navigation is a collection.</param>
public sealed record RelationalNavigation(string Member, string ForeignKey, bool Collection);

/// <summary>A mapped column: the entity property, its stored name and (optionally) its value converter.</summary>
/// <param name="Property">The entity property.</param>
/// <param name="Name">The column name (already through the dialect's naming convention or an explicit override).</param>
/// <param name="Converter">The value converter, or <c>null</c> when the member stores as-is.</param>
public sealed record RelationalColumn(PropertyInfo Property, string Name, RelationalConverter? Converter = null)
{
    private Data.Repository.EntityAccessor? _accessor;
    private bool _accessorResolved;

    /// <summary>The stored CLR type — the converter's, or the member's own.</summary>
    public Type StoredType => Converter?.StoredType ?? Property.PropertyType;

    /// <summary>The maximum text length (0 = the dialect's default), from <c>[Facet]</c> / <c>Facet(...)</c>.</summary>
    public int Length { get; init; }

    /// <summary>The decimal precision (0 = the dialect's default).</summary>
    public int Precision { get; init; }

    /// <summary>The decimal scale (meaningful when <see cref="Precision" /> is set).</summary>
    public int Scale { get; init; }

    /// <summary>
    ///     The stored names this column used to have, from <c>[PreviousName]</c> / <c>PreviousName(...)</c>. A
    ///     model comparison uses them to recognise a rename instead of reading it as a drop and an add.
    /// </summary>
    public IReadOnlyList<string> PreviousNames { get; init; } = [];

    /// <summary>
    ///     The value existing records take when this column is added to a model already in production, from
    ///     <c>[DefaultValue]</c> / <c>Default(...)</c>. <c>null</c> means none was declared, and a comparison that
    ///     needs one says so rather than inventing it.
    /// </summary>
    public object? DefaultValue { get; init; }

    /// <summary>
    ///     Whether the store assigns this column's value on insert (an identity, a sequence, a default), from
    ///     <c>Generated(...)</c>: inserts leave it out and read it back, updates never write it.
    /// </summary>
    public bool IsStoreGenerated { get; init; }

    /// <summary>
    ///     What the store assigns a store-generated column (<see cref="Modeling.StoreValue.Sequence" /> or
    ///     <see cref="Modeling.StoreValue.Clock" />) when no <see cref="StoreDefaultSql" /> is declared.
    /// </summary>
    public Modeling.StoreValue StoreValue { get; init; }

    /// <summary>
    ///     The default expression the store assigns a store-generated column with, as SQL (<c>now()</c>), or
    ///     <c>null</c> for an identity column. Only the migrations read it.
    /// </summary>
    public string? StoreDefaultSql { get; init; }

    /// <summary>Reads the member from an entity as its <b>stored</b> value.</summary>
    public object? Read(object entity)
    {
        // A generated accessor (from the package's source generator) reads without reflection; the lookup
        // resolves once per column and reflection remains the fallback contract.
        if (!_accessorResolved)
        {
            _accessor = Property.DeclaringType is { } declaring ? Data.Repository.EntityAccessors.For(declaring) : null;
            _accessorResolved = true;
        }

        var value = _accessor is not null ? _accessor.Get(entity, Property.Name) : Property.GetValue(entity);
        return Converter is null ? value : Converter.ToStored(value);
    }

    /// <summary>Converts a value that is about to bind against this column into its stored form.</summary>
    public object? Store(object? value) => Converter is null ? value : Converter.ToStored(value);
}

/// <summary>A search-indexed text column: the migration materializes an index serving <c>LIKE</c> where the dialect can.</summary>
/// <param name="Column">The column.</param>
/// <param name="Mode">What the declaration promises to match (<c>Contains</c> serves any pattern).</param>
public sealed record RelationalSearchColumn(RelationalColumn Column, Data.Modeling.SearchMode Mode);

/// <summary>An ordered-read column: the migration materializes a multi-column index with the declared directions.</summary>
/// <param name="Column">The column.</param>
/// <param name="Descending">Whether the declared order is descending.</param>
public sealed record RelationalClusteringColumn(RelationalColumn Column, bool Descending);

/// <summary>
///     The relational mapping for an entity: its table, key column, columns (named through the dialect's
///     convention unless overridden) and whether the key is database-generated. Declared up front — the engine
///     renders SQL from it and never spells a name by hand.
/// </summary>
public abstract class RelationalEntityConfiguration
{
    /// <summary>Initializes the configuration.</summary>
    protected RelationalEntityConfiguration(Type entityType, string tableName, IReadOnlyList<RelationalColumn> columns,
        RelationalColumn key, bool keyIsGenerated, RelationalColumn? concurrencyToken = null,
        IReadOnlyList<RelationalNavigation>? navigations = null, IReadOnlyList<RelationalSearchColumn>? searchColumns = null,
        IReadOnlyList<RelationalColumn>? keys = null, IReadOnlyList<RelationalClusteringColumn>? clusteringColumns = null)
    {
        EntityType = entityType;
        TableName = tableName;
        Columns = columns;
        Key = key;
        KeyIsGenerated = keyIsGenerated;
        ConcurrencyToken = concurrencyToken;
        Navigations = navigations ?? [];
        SearchColumns = searchColumns ?? [];
        Keys = keys is { Count: > 0 } ? keys : [key];
        ClusteringColumns = clusteringColumns ?? [];
    }

    /// <summary>The entity type.</summary>
    public Type EntityType { get; }

    /// <summary>The table name.</summary>
    public string TableName { get; }

    /// <summary>Every mapped column.</summary>
    public IReadOnlyList<RelationalColumn> Columns { get; }

    /// <summary>The key column (the first one when the key is composite — see <see cref="Keys" />).</summary>
    public RelationalColumn Key { get; }

    /// <summary>The key columns, in declared order (one for a simple key; several for <c>Key(x =&gt; new { … })</c>).</summary>
    public IReadOnlyList<RelationalColumn> Keys { get; }

    /// <summary>Whether the key spans more than one column.</summary>
    public bool HasCompositeKey => Keys.Count > 1;

    /// <summary>The ordered-read columns (a multi-column index materializes them).</summary>
    public IReadOnlyList<RelationalClusteringColumn> ClusteringColumns { get; }

    /// <summary>Whether the key is database-generated (identity): inserts omit it and read it back.</summary>
    public bool KeyIsGenerated { get; }

    /// <summary>
    ///     The optimistic-concurrency column, or <c>null</c>: updates and deletes match it in the WHERE and bump
    ///     it, and a commit whose writes miss rows throws <c>ConcurrencyConflictException</c>.
    /// </summary>
    public RelationalColumn? ConcurrencyToken { get; }

    /// <summary>The declared navigations (foreign-key overrides); undeclared ones resolve by convention.</summary>
    public IReadOnlyList<RelationalNavigation> Navigations { get; }

    /// <summary>
    ///     The search-indexed text columns. Relational <c>LIKE</c> already pushes down without them — the
    ///     declaration is about the <b>index</b>: on PostgreSQL the migration materializes a GIN trigram index
    ///     (<c>pg_trgm</c>) so substring matches stop scanning; dialects without an equivalent ignore it (the
    ///     semantics never change, only the plan).
    /// </summary>
    public IReadOnlyList<RelationalSearchColumn> SearchColumns { get; }

    /// <summary>Resolves a declared navigation by member name, or <c>null</c> — conventions apply then.</summary>
    public RelationalNavigation? NavigationFor(string memberName) =>
        Navigations.FirstOrDefault(navigation => string.Equals(navigation.Member, memberName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Resolves a member (property) name to its column, or <c>null</c> when the member is not mapped.</summary>
    public RelationalColumn? ColumnFor(string memberName) =>
        Columns.FirstOrDefault(column => string.Equals(column.Property.Name, memberName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The typed relational mapping for <typeparamref name="TEntity" />.</summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public sealed class RelationalEntityConfiguration<TEntity> : RelationalEntityConfiguration
    where TEntity : class
{
    internal RelationalEntityConfiguration(string tableName, IReadOnlyList<RelationalColumn> columns,
        RelationalColumn key, bool keyIsGenerated, RelationalColumn? concurrencyToken = null,
        IReadOnlyList<RelationalNavigation>? navigations = null, IReadOnlyList<RelationalSearchColumn>? searchColumns = null,
        IReadOnlyList<RelationalColumn>? keys = null, IReadOnlyList<RelationalClusteringColumn>? clusteringColumns = null)
        : base(typeof(TEntity), tableName, columns, key, keyIsGenerated, concurrencyToken, navigations, searchColumns,
            keys, clusteringColumns)
    {
    }
}

/// <summary>The registered relational mappings, keyed by entity type.</summary>
public sealed class RelationalModel
{
    private readonly Dictionary<Type, RelationalEntityConfiguration> _configurations = new();

    /// <summary>The registered configurations.</summary>
    public IReadOnlyDictionary<Type, RelationalEntityConfiguration> Configurations => _configurations;

    /// <summary>Gets the configuration for an entity type, or throws when it was not registered.</summary>
    /// <param name="entityType">The entity type.</param>
    public RelationalEntityConfiguration For(Type entityType) =>
        _configurations.TryGetValue(entityType, out var configuration)
            ? configuration
            : throw new InvalidOperationException(
                $"No relational configuration is registered for '{entityType.Name}'. Register it with Entity<{entityType.Name}>(...).");

    internal void Add(RelationalEntityConfiguration configuration) => _configurations[configuration.EntityType] = configuration;

    /// <summary>
    ///     Describes every mapping decision the model made — what convention decided, what annotations and
    ///     fluent configuration declared — the way <c>Explain()</c> describes a query. The model never lies:
    ///     read this instead of guessing what a column ended up being called.
    /// </summary>
    /// <param name="dialect">The dialect (for stored DDL types).</param>
    public string Explain(SqlDialect dialect)
    {
        var report = new System.Text.StringBuilder();
        foreach (var native in dialect.NativeEnums.OrderBy(entry => entry.EnumType.Name))
        {
            report.AppendLine($"{native.EnumType.Name} -> stored as labels, {dialect.EnumSqlType(native)} (" +
                              string.Join(", ", native.Labels.Select(label => label.Key == label.Value ? label.Key : $"{label.Key} '{label.Value}'")) + ")");
        }

        foreach (var configuration in _configurations.Values.OrderBy(entry => entry.EntityType.Name))
        {
            report.AppendLine($"{configuration.EntityType.Name} -> table \"{configuration.TableName}\"");
            if (configuration.HasCompositeKey)
            {
                report.AppendLine("  key: (" + string.Join(", ",
                    configuration.Keys.Select(key => $"{key.Property.Name} \"{key.Name}\"")) + ") composite, client-generated");
            }
            else
            {
                report.AppendLine($"  key: {configuration.Key.Property.Name} \"{configuration.Key.Name}\" " +
                                  $"({dialect.SqlType(configuration.Key)})" +
                                  (configuration.KeyIsGenerated ? " database-generated" : " client-generated"));
            }

            if (configuration.ConcurrencyToken is { } token)
            {
                report.AppendLine($"  concurrency token: {token.Property.Name} \"{token.Name}\"");
            }

            foreach (var column in configuration.Columns.Where(column => !configuration.Keys.Contains(column)))
            {
                var converted = column.Converter is not null
                    ? $" (converted from {column.Property.PropertyType.Name})"
                    : string.Empty;
                var generated = column.IsStoreGenerated
                    ? column.StoreDefaultSql is { } sql
                        ? $" store-generated (default {sql})"
                        : column.StoreValue == Modeling.StoreValue.Sequence ? " store-generated (sequence)" : " store-generated (clock)"
                    : string.Empty;
                report.AppendLine($"  column: {column.Property.Name} \"{column.Name}\" ({dialect.SqlType(column)}){converted}{generated}");
            }

            if (configuration.ClusteringColumns.Count > 0)
            {
                report.AppendLine("  clustering: " + string.Join(", ", configuration.ClusteringColumns.Select(clustering =>
                    $"{clustering.Column.Name} {(clustering.Descending ? "DESC" : "ASC")}")) + " (multi-column index)");
            }

            foreach (var navigation in configuration.Navigations)
            {
                report.AppendLine($"  navigation: {navigation.Member} ({(navigation.Collection ? "collection" : "reference")} " +
                                  $"via {navigation.ForeignKey})");
            }

            foreach (var search in configuration.SearchColumns)
            {
                var materialized = dialect.SearchIndexSql("probe", "probe", "probe").Count > 0;
                report.AppendLine($"  search index: {search.Column.Property.Name} \"{search.Column.Name}\" " +
                                  (materialized
                                      ? "(GIN trigram via pg_trgm; LIKE stops scanning)"
                                      : "(no index structure on this dialect; LIKE still pushes down, unindexed)"));
            }

            if (eQuantic.Core.Data.Repository.EntityLifecycle.IsSoftDelete(
                    configuration.EntityType, new eQuantic.Core.Data.Repository.DataConventions()))
            {
                report.AppendLine("  lifecycle: soft delete + live-rows filter (IEntityTimeEnded)");
            }
        }

        return report.ToString();
    }
}

/// <summary>Fluent builder for the <see cref="RelationalModel" /> — one <c>Entity</c> call per mapped type.</summary>
public sealed class RelationalModelBuilder
{
    private readonly RelationalModel _model = new();
    private readonly SqlDialect _dialect;

    internal RelationalModelBuilder(SqlDialect dialect) => _dialect = dialect;

    /// <summary>
    ///     Declares <typeparamref name="TEnum" /> stored as its labels, as <see cref="Modeling.EnumTypeAttribute" />
    ///     does on the enum: written, read and filtered by each member's <c>[StoredAs]</c> label (or its own name),
    ///     in the dialect's own column (a PostgreSQL enum type, a MySQL <c>ENUM(...)</c>, or text the labels
    ///     restrict), which the migrations create. An enum declared neither way is stored as its number.
    /// </summary>
    /// <typeparam name="TEnum">The enum.</typeparam>
    /// <param name="storedName">The store's type name; the dialect's naming of the enum's name when omitted.</param>
    /// <returns>The same builder for chaining.</returns>
    public RelationalModelBuilder Enum<TEnum>(string? storedName = null) where TEnum : struct, global::System.Enum
    {
        _dialect.DeclareNativeEnum(RelationalNativeEnum.Describe<TEnum>(storedName ?? _dialect.TableName(typeof(TEnum).Name)));
        return this;
    }

    /// <summary>Maps <typeparamref name="TEntity" /> to a table and key.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="configure">The fluent configuration.</param>
    /// <returns>The same builder for chaining.</returns>
    public RelationalModelBuilder Entity<TEntity>(Action<RelationalEntityBuilder<TEntity>> configure) where TEntity : class
    {
        var builder = new RelationalEntityBuilder<TEntity>(_dialect);
        configure(builder);
        _model.Add(builder.Build());
        return this;
    }

    internal RelationalModel Build() => _model;
}

/// <summary>Fluent configuration for one entity's relational mapping.</summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public sealed class RelationalEntityBuilder<TEntity> where TEntity : class
{
    private readonly SqlDialect _dialect;
    private readonly Dictionary<string, string> _columnOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RelationalConverter> _converters = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RelationalNavigation> _navigations = [];
    private readonly Dictionary<string, Modeling.SearchMode> _searchMembers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Member, bool Descending)> _clusteringMembers = [];
    private readonly Dictionary<string, (int Length, int Precision, int Scale)> _facets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _previousNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object?> _defaults = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (Modeling.StoreValue Value, string? Sql)> _generated = new(StringComparer.OrdinalIgnoreCase);
    private List<string>? _keyMembers;
    private string? _table;
    private string? _keyMember;
    private bool _keyIsGenerated;
    private string? _concurrencyMember;

    internal RelationalEntityBuilder(SqlDialect dialect)
    {
        _dialect = dialect;

        // The eQuantic.Core.Data.Modeling annotations seed the builder; fluent calls override them
        // (conventions < annotations < fluent). Annotations outside the relational vocabulary are ignored.
        if (Modeling.EntityAttribute.NameFor(typeof(TEntity)) is { } name)
        {
            _table = name;
        }

        foreach (var property in typeof(TEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetCustomAttribute<Modeling.UnmappedAttribute>() is not null)
            {
                _ignored.Add(property.Name);
            }

            if (property.GetCustomAttribute<Modeling.StoredAsAttribute>() is { } stored)
            {
                _columnOverrides[property.Name] = stored.Name;
            }

            if (property.GetCustomAttribute<Modeling.EntityKeyAttribute>() is { } key)
            {
                _keyMember = property.Name;
                _keyIsGenerated = key.Generated;
            }

            if (property.GetCustomAttribute<Modeling.ConcurrencyTokenAttribute>() is not null)
            {
                _concurrencyMember = property.Name;
            }

            if (property.GetCustomAttribute<Modeling.SearchIndexAttribute>() is { } search)
            {
                _searchMembers[property.Name] = search.Mode;
            }

            if (property.GetCustomAttribute<Modeling.FacetAttribute>() is { } facet)
            {
                _facets[property.Name] = (facet.Length, facet.Precision, facet.Scale);
            }

            var renames = property.GetCustomAttributes<Modeling.PreviousNameAttribute>().ToList();
            if (renames.Count > 0)
            {
                _previousNames[property.Name] = renames.Select(previous => previous.Name).ToList();
            }

            if (property.GetCustomAttribute<Modeling.DefaultValueAttribute>() is { } fallback)
            {
                _defaults[property.Name] = fallback.Value;
            }

            if (property.GetCustomAttribute<Modeling.StoreGeneratedAttribute>() is { } generated)
            {
                _generated[property.Name] = (generated.Value, null);
            }
        }

        foreach (var property in typeof(TEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(candidate => candidate.GetCustomAttribute<Modeling.ClusteringKeyAttribute>() is not null)
                     .OrderBy(candidate => candidate.GetCustomAttribute<Modeling.ClusteringKeyAttribute>()!.Order))
        {
            _clusteringMembers.Add((property.Name, property.GetCustomAttribute<Modeling.ClusteringKeyAttribute>()!.Descending));
        }
    }

    /// <summary>Sets the table name (defaults to the entity type name through the dialect's naming convention).</summary>
    /// <param name="name">The table name.</param>
    public RelationalEntityBuilder<TEntity> Table(string name)
    {
        _table = name;
        return this;
    }

    /// <summary>
    ///     Declares the key member (defaults to a member named <c>Id</c>) — or a <b>composite key</b> with an
    ///     anonymous projection: <c>Key(x =&gt; new { x.OrderId, x.LineNumber })</c>, member order defining the
    ///     column order. Point lookups take the same shape as a tuple (<c>GetAsync((orderId, lineNumber))</c>);
    ///     a composite key cannot be database-generated.
    /// </summary>
    /// <typeparam name="TKey">The member (or anonymous projection) type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="generated">Whether the database generates the key (identity): inserts omit it and read it back.</param>
    public RelationalEntityBuilder<TEntity> Key<TKey>(Expression<Func<TEntity, TKey>> selector, bool generated = false)
    {
        if (selector.Body is NewExpression { Arguments.Count: > 1 } projection)
        {
            if (generated)
            {
                throw new InvalidOperationException(
                    $"Entity '{typeof(TEntity).Name}': a composite key cannot be database-generated.");
            }

            _keyMembers = projection.Arguments
                .Select(argument => argument is MemberExpression member
                    ? member.Member.Name
                    : throw new InvalidOperationException("A composite key projects entity members only (new { x.A, x.B })."))
                .ToList();
            _keyMember = _keyMembers[0];
            _keyIsGenerated = false;
            return this;
        }

        _keyMembers = null;
        _keyMember = selector.GetMemberName();
        _keyIsGenerated = generated;
        return this;
    }

    /// <summary>
    ///     Declares an ordered-read member — "I read this sorted" — materialized as a multi-column index with
    ///     the declared directions by the migration's <c>EnsureCollection()</c>. Call in order for several.
    /// </summary>
    /// <typeparam name="TMember">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="descending">Whether the declared order is descending.</param>
    public RelationalEntityBuilder<TEntity> ClusteringKey<TMember>(Expression<Func<TEntity, TMember>> selector, bool descending = false)
    {
        _clusteringMembers.Add((selector.GetMemberName(), descending));
        return this;
    }

    /// <summary>Declares storage facets for a member: a text length, a decimal precision/scale (the DDL sizes the column).</summary>
    /// <typeparam name="TMember">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="length">The maximum text length (0 = the dialect's default).</param>
    /// <param name="precision">The decimal precision (0 = the dialect's default).</param>
    /// <param name="scale">The decimal scale (with <paramref name="precision" />).</param>
    public RelationalEntityBuilder<TEntity> Facet<TMember>(Expression<Func<TEntity, TMember>> selector,
        int length = 0, int precision = 0, int scale = 0)
    {
        _facets[selector.GetMemberName()] = (length, precision, scale);
        return this;
    }

    /// <summary>Overrides the stored column name for a member (the dialect's naming convention applies otherwise).</summary>
    /// <typeparam name="TMember">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="columnName">The column name.</param>
    public RelationalEntityBuilder<TEntity> Column<TMember>(Expression<Func<TEntity, TMember>> selector, string columnName)
    {
        _columnOverrides[selector.GetMemberName()] = columnName;
        return this;
    }

    /// <summary>
    ///     Records a stored name this member used to have, so a rename reads as a rename rather than as one
    ///     column dropped and another added — which is the difference between keeping the data and losing it.
    ///     Call it more than once for a member renamed more than once.
    /// </summary>
    /// <typeparam name="TMember">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="previousName">The stored name it used to have.</param>
    public RelationalEntityBuilder<TEntity> PreviousName<TMember>(Expression<Func<TEntity, TMember>> selector, string previousName)
    {
        var member = selector.GetMemberName();
        if (!_previousNames.TryGetValue(member, out var names))
        {
            _previousNames[member] = names = [];
        }

        names.Add(previousName);
        return this;
    }

    /// <summary>
    ///     The value existing records take when this member is added to a model already in production. Without
    ///     it, adding a member produces a change that refuses to run: the value is a domain decision and the
    ///     engine will not guess it — see <c>[DefaultValue]</c> for what that costs on a document store.
    /// </summary>
    /// <typeparam name="TMember">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="value">The value existing records take.</param>
    public RelationalEntityBuilder<TEntity> Default<TMember>(Expression<Func<TEntity, TMember>> selector, TMember value)
    {
        _defaults[selector.GetMemberName()] = value;
        return this;
    }

    /// <summary>
    ///     Declares a column outside the key whose value the store assigns on insert, as
    ///     <see cref="Modeling.StoreGeneratedAttribute" /> does: the next number of a sequence for an integral member,
    ///     the store's clock for a date (or the kind given). Inserts leave the column out and read the store's value
    ///     back onto the entity, updates never write it, and a bulk insert leaves it to the store.
    /// </summary>
    /// <typeparam name="TMember">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="value">What the store assigns; inferred from the member's type by default.</param>
    public RelationalEntityBuilder<TEntity> Generated<TMember>(Expression<Func<TEntity, TMember>> selector,
        Modeling.StoreValue value = Modeling.StoreValue.Inferred)
    {
        _generated[selector.GetMemberName()] = (value, null);
        return this;
    }

    /// <summary>
    ///     Declares a store-generated column whose default is the given SQL expression (a relational escape hatch
    ///     for what no <see cref="Modeling.StoreValue" /> says, such as a schema another tool created). It behaves as
    ///     any store-generated column: left out of inserts, read back, never written by an update.
    /// </summary>
    /// <typeparam name="TMember">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="defaultSql">The store's default expression, as SQL in this dialect.</param>
    public RelationalEntityBuilder<TEntity> Generated<TMember>(Expression<Func<TEntity, TMember>> selector, string defaultSql)
    {
        if (string.IsNullOrWhiteSpace(defaultSql))
        {
            throw new ArgumentException("A store default needs its SQL expression.", nameof(defaultSql));
        }

        _generated[selector.GetMemberName()] = (Modeling.StoreValue.Inferred, defaultSql);
        return this;
    }

    /// <summary>Excludes a member from the mapping (navigations are excluded automatically; this is for the rest).</summary>
    /// <typeparam name="TMember">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    public RelationalEntityBuilder<TEntity> Ignore<TMember>(Expression<Func<TEntity, TMember>> selector)
    {
        _ignored.Add(selector.GetMemberName());
        return this;
    }

    /// <summary>
    ///     Declares a <b>reference navigation with an explicit foreign key</b> — for schemas the
    ///     <c>{Nav}Id</c> convention does not fit. <c>Include</c> loads it with one follow-up <c>IN</c> query.
    /// </summary>
    /// <typeparam name="TTarget">The referenced entity type.</typeparam>
    /// <param name="navigation">The navigation selector.</param>
    /// <param name="foreignKey">The foreign-key member on <b>this</b> entity.</param>
    public RelationalEntityBuilder<TEntity> Reference<TTarget>(
        Expression<Func<TEntity, TTarget?>> navigation, Expression<Func<TEntity, object?>> foreignKey)
        where TTarget : class
    {
        _navigations.Add(new RelationalNavigation(navigation.GetMemberName(), foreignKey.GetMemberName(), Collection: false));
        return this;
    }

    /// <summary>
    ///     Declares a <b>collection navigation with an explicit foreign key</b> — for schemas the
    ///     <c>{Entity}Id</c> convention does not fit. <c>Include</c> loads it with one follow-up <c>IN</c> query.
    /// </summary>
    /// <typeparam name="TElement">The element entity type.</typeparam>
    /// <param name="navigation">The navigation selector.</param>
    /// <param name="foreignKey">The foreign-key member on the <b>element</b>, pointing back at this entity's key.</param>
    public RelationalEntityBuilder<TEntity> Collection<TElement>(
        Expression<Func<TEntity, IEnumerable<TElement>?>> navigation, Expression<Func<TElement, object?>> foreignKey)
        where TElement : class
    {
        _navigations.Add(new RelationalNavigation(navigation.GetMemberName(), foreignKey.GetMemberName(), Collection: true));
        return this;
    }

    /// <summary>
    ///     Declares a <b>value converter</b>: the member (a Value Object, an enum-as-string — any domain type)
    ///     stores as the scalar <typeparamref name="TStored" />. The conversion applies everywhere the member
    ///     crosses the engine — DDL column type, inserts and updates (set-based included), filter values and
    ///     materialization — so the domain type never leaks to the driver.
    /// </summary>
    /// <typeparam name="TMember">The member type.</typeparam>
    /// <typeparam name="TStored">The stored scalar type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="toStored">Converts the member value into the stored value.</param>
    /// <param name="fromStored">Converts the stored value back into the member value.</param>
    public RelationalEntityBuilder<TEntity> Converts<TMember, TStored>(
        Expression<Func<TEntity, TMember>> selector,
        Func<TMember, TStored> toStored,
        Func<TStored, TMember> fromStored)
    {
        _converters[selector.GetMemberName()] = new RelationalConverter(
            typeof(TStored),
            value => value is null ? null : toStored((TMember)value),
            value => value is null ? null : fromStored((TStored)value));
        return this;
    }

    /// <summary>
    ///     Declares the <b>optimistic-concurrency token</b> (an <c>int</c>, <c>long</c> or <c>Guid</c> member):
    ///     every update and delete of the entity matches the token it read (<c>WHERE … AND version = @old</c>)
    ///     and bumps it; a commit whose writes miss rows throws <c>ConcurrencyConflictException</c> and rolls
    ///     back — the lost update is caught, not silently overwritten.
    /// </summary>
    /// <typeparam name="TMember">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    public RelationalEntityBuilder<TEntity> ConcurrencyToken<TMember>(Expression<Func<TEntity, TMember>> selector)
    {
        _concurrencyMember = selector.GetMemberName();
        return this;
    }

    /// <summary>
    ///     Declares a search-indexed text member. Relational <c>LIKE</c> already pushes down — this is about the
    ///     <b>index</b>: on PostgreSQL the migration's <c>EnsureCollection()</c> materializes a GIN trigram index
    ///     (<c>pg_trgm</c>) so <c>Contains</c>/<c>EndsWith</c> stop scanning; dialects without an equivalent
    ///     ignore the declaration (same semantics, unindexed plan — <c>Explain()</c> says which).
    /// </summary>
    /// <param name="selector">The member selector.</param>
    /// <param name="mode">What the search should serve (<see cref="Modeling.SearchMode.Contains" /> by default).</param>
    public RelationalEntityBuilder<TEntity> SearchIndex(Expression<Func<TEntity, string?>> selector,
        Modeling.SearchMode mode = Modeling.SearchMode.Contains)
    {
        _searchMembers[selector.GetMemberName()] = mode;
        return this;
    }

    internal RelationalEntityConfiguration<TEntity> Build()
    {
        // A JSON member the dialect cannot store would otherwise fall out of the mapping without a word: the
        // insert writes NULL, the read returns nothing, and nothing fails. Refused here instead, by name.
        foreach (var property in typeof(TEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property is { CanRead: true, CanWrite: true } && Modeling.JsonMembers.Is(property.PropertyType)
                && !_ignored.Contains(property.Name) && !_converters.ContainsKey(property.Name)
                && !_dialect.IsDocumentColumn(property.PropertyType))
            {
                throw new NotSupportedException(
                    $"Entity '{typeof(TEntity).Name}' has the JSON member '{property.Name}', and {_dialect.System} has no " +
                    "JSON document column to store it in. Map it with Converts(...) to a string, or Ignore(...) it.");
            }
        }

        // Scalar members (and collections of scalars, for stores with array columns) map to columns; entity
        // references and entity collections are navigations — loaded through Include, never selected.
        var columns = typeof(TEntity)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property is { CanRead: true, CanWrite: true }
                               && property.GetIndexParameters().Length == 0
                               && !_ignored.Contains(property.Name)
                               && (IsMapped(property.PropertyType) || _converters.ContainsKey(property.Name)))
            .Select(property =>
            {
                var facet = _facets.TryGetValue(property.Name, out var declared) ? declared : default;
                return new RelationalColumn(property,
                    _columnOverrides.TryGetValue(property.Name, out var explicitName) ? explicitName : _dialect.ColumnName(property.Name),
                    _converters.TryGetValue(property.Name, out var converter) ? converter : null)
                {
                    Length = facet.Length,
                    Precision = facet.Precision,
                    Scale = facet.Scale,
                    PreviousNames = _previousNames.TryGetValue(property.Name, out var renamed) ? renamed : [],
                    DefaultValue = _defaults.TryGetValue(property.Name, out var fallback) ? fallback : null,
                    IsStoreGenerated = _generated.ContainsKey(property.Name),
                    StoreValue = _generated.TryGetValue(property.Name, out var generated) && generated.Sql is null
                        ? Modeling.StoreGeneratedAttribute.Resolve(generated.Value,
                            _converters.TryGetValue(property.Name, out var stored) ? stored.StoredType : property.PropertyType,
                            $"{typeof(TEntity).Name}.{property.Name}")
                        : Modeling.StoreValue.Inferred,
                    StoreDefaultSql = generated.Sql,
                };
            })
            .ToList();

        foreach (var member in _generated.Keys)
        {
            if (columns.All(candidate => !string.Equals(candidate.Property.Name, member, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Entity '{typeof(TEntity).Name}' has no mapped member '{member}' to declare store-generated.");
            }

            if (!_dialect.ReadsBackInsertedValues)
            {
                throw new NotSupportedException(
                    $"'{typeof(TEntity).Name}.{member}' is declared store-generated, and {_dialect.System} cannot read a value " +
                    "back from an insert. Assign the value on the client instead.");
            }

            if (_dialect.ReadsBackByKey && _keyIsGenerated)
            {
                throw new NotSupportedException(
                    $"'{typeof(TEntity).Name}.{member}' is declared store-generated, and {_dialect.System} reads it back by the " +
                    "row's key, which the store generates here and cannot hand back. Assign the key on the client " +
                    "(a Guid, for one) and the store-generated members are read back.");
            }
        }

        // An enum declared stored as its labels on its own type ([EnumType]) is declared here, the first time a
        // column uses it; Enum<TEnum>(...) on the model builder declares it ahead and wins.
        foreach (var column in columns.Where(column => column.Converter is null))
        {
            if (RelationalNativeEnum.EnumOf(column.StoredType) is { } enumType && _dialect.NativeEnumOf(enumType) is null
                && Modeling.EnumTypeAttribute.For(enumType) is { } declared)
            {
                _dialect.DeclareNativeEnum(RelationalNativeEnum.Describe(enumType, declared.Name ?? _dialect.TableName(enumType.Name)));
            }
        }

        foreach (var converted in _converters)
        {
            if (columns.All(column => !string.Equals(column.Property.Name, converted.Key, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Entity '{typeof(TEntity).Name}' has no member '{converted.Key}' to convert.");
            }

            if (!IsScalar(converted.Value.StoredType))
            {
                throw new InvalidOperationException(
                    $"The converter for '{converted.Key}' must store a scalar type; '{converted.Value.StoredType.Name}' is not.");
            }
        }

        var keyMember = _keyMember ?? "Id";
        var key = columns.FirstOrDefault(column => string.Equals(column.Property.Name, keyMember, StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidOperationException(
                      $"Entity '{typeof(TEntity).Name}' has no mapped member '{keyMember}'; declare the key with Key(x => ...).");

        RelationalColumn? concurrencyToken = null;
        if (_concurrencyMember is not null)
        {
            concurrencyToken = columns.FirstOrDefault(column =>
                                   string.Equals(column.Property.Name, _concurrencyMember, StringComparison.OrdinalIgnoreCase))
                               ?? throw new InvalidOperationException(
                                   $"Entity '{typeof(TEntity).Name}' has no mapped member '{_concurrencyMember}' to use as the concurrency token.");
            var type = Nullable.GetUnderlyingType(concurrencyToken.Property.PropertyType) ?? concurrencyToken.Property.PropertyType;
            if (type != typeof(int) && type != typeof(long) && type != typeof(Guid))
            {
                throw new InvalidOperationException(
                    $"The concurrency token '{_concurrencyMember}' must be an int, long or Guid (it is bumped on every write).");
            }
        }

        var searchColumns = new List<RelationalSearchColumn>();
        foreach (var (member, mode) in _searchMembers)
        {
            var column = columns.FirstOrDefault(candidate =>
                             string.Equals(candidate.Property.Name, member, StringComparison.OrdinalIgnoreCase))
                         ?? throw new InvalidOperationException(
                             $"Entity '{typeof(TEntity).Name}' has no mapped member '{member}' to search-index.");
            searchColumns.Add(new RelationalSearchColumn(column, mode));
        }

        var keys = (_keyMembers ?? [keyMember])
            .Select(member => columns.FirstOrDefault(candidate =>
                                  string.Equals(candidate.Property.Name, member, StringComparison.OrdinalIgnoreCase))
                              ?? throw new InvalidOperationException(
                                  $"Entity '{typeof(TEntity).Name}' has no mapped member '{member}' in the composite key."))
            .ToList();

        if (keys.Concat(concurrencyToken is null ? [] : [concurrencyToken]).FirstOrDefault(column => column.IsStoreGenerated) is { } misplaced)
        {
            throw new InvalidOperationException(
                $"'{typeof(TEntity).Name}.{misplaced.Property.Name}' is a key or the concurrency token, and cannot also be " +
                "declared store-generated. A key the store generates is declared with Key(x => x.Member, generated: true).");
        }

        var clusteringColumns = _clusteringMembers
            .Select(declared => new RelationalClusteringColumn(
                columns.FirstOrDefault(candidate =>
                    string.Equals(candidate.Property.Name, declared.Member, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Entity '{typeof(TEntity).Name}' has no mapped member '{declared.Member}' to cluster by."),
                declared.Descending))
            .ToList();

        return new RelationalEntityConfiguration<TEntity>(
            _table ?? _dialect.TableName(typeof(TEntity).Name), columns, keys[0], _keyIsGenerated, concurrencyToken, _navigations,
            searchColumns, keys, clusteringColumns);
    }

    private bool IsMapped(Type type)
    {
        if (IsScalar(type) || _dialect.IsDocumentColumn(type))
        {
            return true;
        }

        if (type == typeof(string) || type == typeof(byte[]) || !type.IsGenericType && !type.IsArray)
        {
            return false;
        }

        var element = type.IsArray
            ? type.GetElementType()
            : type.GetInterfaces().Append(type)
                .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                ?.GetGenericArguments()[0];
        return element is not null && IsScalar(element);
    }

    private static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum
               || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid)
               || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan)
               || type == typeof(byte[]);
    }
}
