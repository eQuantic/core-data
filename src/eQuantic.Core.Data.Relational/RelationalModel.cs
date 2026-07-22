using System.Linq.Expressions;
using System.Reflection;
using eQuantic.Linq.Expressions;

namespace eQuantic.Core.Data.Relational;

/// <summary>A mapped column: the entity property and its stored name.</summary>
/// <param name="Property">The entity property.</param>
/// <param name="Name">The column name (already through the dialect's naming convention or an explicit override).</param>
public sealed record RelationalColumn(PropertyInfo Property, string Name);

/// <summary>
///     The relational mapping for an entity: its table, key column, columns (named through the dialect's
///     convention unless overridden) and whether the key is database-generated. Declared up front — the engine
///     renders SQL from it and never spells a name by hand.
/// </summary>
public abstract class RelationalEntityConfiguration
{
    /// <summary>Initializes the configuration.</summary>
    protected RelationalEntityConfiguration(Type entityType, string tableName, IReadOnlyList<RelationalColumn> columns,
        RelationalColumn key, bool keyIsGenerated, RelationalColumn? concurrencyToken = null)
    {
        EntityType = entityType;
        TableName = tableName;
        Columns = columns;
        Key = key;
        KeyIsGenerated = keyIsGenerated;
        ConcurrencyToken = concurrencyToken;
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
        RelationalColumn key, bool keyIsGenerated, RelationalColumn? concurrencyToken = null)
        : base(typeof(TEntity), tableName, columns, key, keyIsGenerated, concurrencyToken)
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
    private string? _table;
    private string? _keyMember;
    private bool _keyIsGenerated;
    private string? _concurrencyMember;

    internal RelationalEntityBuilder(SqlDialect dialect) => _dialect = dialect;

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
                               && IsMapped(property.PropertyType))
            .Select(property => new RelationalColumn(property,
                _columnOverrides.TryGetValue(property.Name, out var explicitName) ? explicitName : _dialect.ColumnName(property.Name)))
            .ToList();

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
            _table ?? _dialect.TableName(typeof(TEntity).Name), columns, key, _keyIsGenerated, concurrencyToken);
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
