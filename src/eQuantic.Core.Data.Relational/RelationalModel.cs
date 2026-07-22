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
    /// <summary>The stored CLR type — the converter's, or the member's own.</summary>
    public Type StoredType => Converter?.StoredType ?? Property.PropertyType;

    /// <summary>Reads the member from an entity as its <b>stored</b> value.</summary>
    public object? Read(object entity)
    {
        var value = Property.GetValue(entity);
        return Converter is null ? value : Converter.ToStored(value);
    }

    /// <summary>Converts a value that is about to bind against this column into its stored form.</summary>
    public object? Store(object? value) => Converter is null ? value : Converter.ToStored(value);
}

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
        IReadOnlyList<RelationalNavigation>? navigations = null)
    {
        EntityType = entityType;
        TableName = tableName;
        Columns = columns;
        Key = key;
        KeyIsGenerated = keyIsGenerated;
        ConcurrencyToken = concurrencyToken;
        Navigations = navigations ?? [];
    }

    /// <summary>The entity type.</summary>
    public Type EntityType { get; }

    /// <summary>The table name.</summary>
    public string TableName { get; }

    /// <summary>Every mapped column.</summary>
    public IReadOnlyList<RelationalColumn> Columns { get; }

    /// <summary>The key column.</summary>
    public RelationalColumn Key { get; }

    /// <summary>Whether the key is database-generated (identity): inserts omit it and read it back.</summary>
    public bool KeyIsGenerated { get; }

    /// <summary>
    ///     The optimistic-concurrency column, or <c>null</c>: updates and deletes match it in the WHERE and bump
    ///     it, and a commit whose writes miss rows throws <c>ConcurrencyConflictException</c>.
    /// </summary>
    public RelationalColumn? ConcurrencyToken { get; }

    /// <summary>The declared navigations (foreign-key overrides); undeclared ones resolve by convention.</summary>
    public IReadOnlyList<RelationalNavigation> Navigations { get; }

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
        IReadOnlyList<RelationalNavigation>? navigations = null)
        : base(typeof(TEntity), tableName, columns, key, keyIsGenerated, concurrencyToken, navigations)
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
        foreach (var configuration in _configurations.Values.OrderBy(entry => entry.EntityType.Name))
        {
            report.AppendLine($"{configuration.EntityType.Name} -> table \"{configuration.TableName}\"");
            report.AppendLine($"  key: {configuration.Key.Property.Name} \"{configuration.Key.Name}\" " +
                              $"({dialect.SqlType(configuration.Key.StoredType)})" +
                              (configuration.KeyIsGenerated ? " database-generated" : " client-generated"));
            if (configuration.ConcurrencyToken is { } token)
            {
                report.AppendLine($"  concurrency token: {token.Property.Name} \"{token.Name}\"");
            }

            foreach (var column in configuration.Columns.Where(column => column != configuration.Key))
            {
                var converted = column.Converter is not null
                    ? $" (converted from {column.Property.PropertyType.Name})"
                    : string.Empty;
                report.AppendLine($"  column: {column.Property.Name} \"{column.Name}\" ({dialect.SqlType(column.StoredType)}){converted}");
            }

            foreach (var navigation in configuration.Navigations)
            {
                report.AppendLine($"  navigation: {navigation.Member} ({(navigation.Collection ? "collection" : "reference")} " +
                                  $"via {navigation.ForeignKey})");
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
        }
    }

    /// <summary>Sets the table name (defaults to the entity type name through the dialect's naming convention).</summary>
    /// <param name="name">The table name.</param>
    public RelationalEntityBuilder<TEntity> Table(string name)
    {
        _table = name;
        return this;
    }

    /// <summary>Declares the key member (defaults to a member named <c>Id</c>).</summary>
    /// <typeparam name="TKey">The member type.</typeparam>
    /// <param name="selector">The member selector.</param>
    /// <param name="generated">Whether the database generates the key (identity): inserts omit it and read it back.</param>
    public RelationalEntityBuilder<TEntity> Key<TKey>(Expression<Func<TEntity, TKey>> selector, bool generated = false)
    {
        _keyMember = selector.GetMemberName();
        _keyIsGenerated = generated;
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

    internal RelationalEntityConfiguration<TEntity> Build()
    {
        // Scalar members (and collections of scalars, for stores with array columns) map to columns; entity
        // references and entity collections are navigations — loaded through Include, never selected.
        var columns = typeof(TEntity)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property is { CanRead: true, CanWrite: true }
                               && property.GetIndexParameters().Length == 0
                               && !_ignored.Contains(property.Name)
                               && (IsMapped(property.PropertyType) || _converters.ContainsKey(property.Name)))
            .Select(property => new RelationalColumn(property,
                _columnOverrides.TryGetValue(property.Name, out var explicitName) ? explicitName : _dialect.ColumnName(property.Name),
                _converters.TryGetValue(property.Name, out var converter) ? converter : null))
            .ToList();

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

        return new RelationalEntityConfiguration<TEntity>(
            _table ?? _dialect.TableName(typeof(TEntity).Name), columns, key, _keyIsGenerated, concurrencyToken, _navigations);
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
