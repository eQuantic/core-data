using System.Linq.Expressions;
using eQuantic.Linq.Expressions;
using Microsoft.Azure.Cosmos;

namespace eQuantic.Core.Data.CosmosDb;

/// <summary>
///     The Cosmos mapping for a single entity: which container it lives in, its partition key (path plus a
///     selector to read the value from an instance for point writes), and container options such as TTL. Unlike
///     MongoDB, Cosmos needs the partition key on every point read/write, so it must be declared up front.
/// </summary>
public abstract class CosmosEntityConfiguration
{
    /// <summary>The entity type.</summary>
    public Type EntityType { get; }

    /// <summary>The container name.</summary>
    public string ContainerName { get; }

    /// <summary>The partition key path (e.g. <c>/countryCode</c>).</summary>
    public string PartitionKeyPath { get; }

    /// <summary>The container's default time-to-live in seconds, or <c>null</c> for none.</summary>
    public int? DefaultTimeToLiveSeconds { get; protected init; }

    /// <summary>Where the document id comes from (for <see cref="CosmosModel.Explain" />).</summary>
    public string IdDescription { get; protected init; } = "Id (convention)";

    /// <summary>Whether the mapping declares a concurrency token (the document's <c>_etag</c>).</summary>
    public bool HasConcurrencyToken { get; protected init; }

    /// <summary>Initializes the configuration.</summary>
    protected CosmosEntityConfiguration(Type entityType, string containerName, string partitionKeyPath)
    {
        EntityType = entityType;
        ContainerName = containerName;
        PartitionKeyPath = partitionKeyPath;
    }

    /// <summary>Reads the partition key value from an entity instance (for point writes, patches and deletes).</summary>
    /// <param name="entity">The entity.</param>
    public abstract PartitionKey GetPartitionKey(object entity);

    /// <summary>Reads the Cosmos document id (always a string) from an entity instance.</summary>
    /// <param name="entity">The entity.</param>
    public abstract string GetId(object entity);

    /// <summary>
    ///     Reads the concurrency token (the document's <c>_etag</c>) from an entity instance, or <c>null</c> when
    ///     the entity declares none / carries none. When present, a <c>Modify</c>/<c>Merge</c> stages a
    ///     conditional replace (<c>If-Match</c>) instead of an unconditional upsert.
    /// </summary>
    /// <param name="entity">The entity.</param>
    public virtual string? GetETag(object entity) => null;
}

/// <summary>The typed Cosmos mapping for <typeparamref name="TEntity" />.</summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public sealed class CosmosEntityConfiguration<TEntity> : CosmosEntityConfiguration
    where TEntity : class
{
    private readonly Func<TEntity, PartitionKey> _partitionKey;
    private readonly Func<TEntity, string> _id;
    private readonly Func<TEntity, string?>? _etag;

    internal CosmosEntityConfiguration(string containerName, string partitionKeyPath,
        Func<TEntity, PartitionKey> partitionKey, Func<TEntity, string> id, int? ttlSeconds,
        Func<TEntity, string?>? etag = null, string? idDescription = null)
        : base(typeof(TEntity), containerName, partitionKeyPath)
    {
        _partitionKey = partitionKey;
        _id = id;
        _etag = etag;
        DefaultTimeToLiveSeconds = ttlSeconds;
        HasConcurrencyToken = etag is not null;
        if (idDescription is not null)
        {
            IdDescription = idDescription;
        }
    }

    /// <inheritdoc />
    public override PartitionKey GetPartitionKey(object entity) => _partitionKey((TEntity)entity);

    /// <inheritdoc />
    public override string GetId(object entity) => _id((TEntity)entity);

    /// <inheritdoc />
    public override string? GetETag(object entity)
    {
        var etag = _etag?.Invoke((TEntity)entity);
        return string.IsNullOrEmpty(etag) ? null : etag;
    }
}

/// <summary>The registered Cosmos mappings, keyed by entity type.</summary>
public sealed class CosmosModel
{
    private readonly Dictionary<Type, CosmosEntityConfiguration> _configurations = new();
    private readonly List<System.Text.Json.Serialization.JsonConverter> _converters = [];

    /// <summary>The registered configurations.</summary>
    public IReadOnlyDictionary<Type, CosmosEntityConfiguration> Configurations => _configurations;

    /// <summary>The value converters the model declared (joined into the serializer by <see cref="CosmosClientFactory" />).</summary>
    public IReadOnlyList<System.Text.Json.Serialization.JsonConverter> Converters => _converters;

    internal void AddConverter(System.Text.Json.Serialization.JsonConverter converter) => _converters.Add(converter);

    /// <summary>Gets the configuration for an entity type, or throws when it was not registered.</summary>
    /// <param name="entityType">The entity type.</param>
    public CosmosEntityConfiguration For(Type entityType) =>
        _configurations.TryGetValue(entityType, out var configuration)
            ? configuration
            : throw new InvalidOperationException(
                $"No Cosmos configuration is registered for '{entityType.Name}'. Register it with Entity<{entityType.Name}>(...).");

    internal void Add(CosmosEntityConfiguration configuration) => _configurations[configuration.EntityType] = configuration;

    /// <summary>
    ///     Describes every mapping decision the model made — container, partition key path, document id source,
    ///     TTL and concurrency — the way <c>Explain()</c> describes a query. Read this instead of guessing what
    ///     the mapping ended up being.
    /// </summary>
    public string Explain()
    {
        var report = new System.Text.StringBuilder();
        foreach (var configuration in _configurations.Values.OrderBy(entry => entry.EntityType.Name))
        {
            report.AppendLine($"{configuration.EntityType.Name} -> container \"{configuration.ContainerName}\"");
            report.AppendLine($"  partition key: \"{configuration.PartitionKeyPath}\"");
            report.AppendLine($"  id: {configuration.IdDescription}");
            if (configuration.DefaultTimeToLiveSeconds is { } ttl)
            {
                report.AppendLine($"  default TTL: {ttl}s (container-level; documents expire unless they override it)");
            }

            if (configuration.HasConcurrencyToken)
            {
                report.AppendLine("  concurrency token: _etag (writes replace conditionally with If-Match)");
            }
        }

        return report.ToString();
    }
}

/// <summary>Fluent builder for the <see cref="CosmosModel" /> — one <c>Entity</c> call per mapped type.</summary>
public sealed class CosmosModelBuilder
{
    private readonly CosmosModel _model = new();

    /// <summary>Maps <typeparamref name="TEntity" /> to a container and partition key.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="configure">The fluent configuration.</param>
    /// <returns>The same builder for chaining.</returns>
    public CosmosModelBuilder Entity<TEntity>(Action<CosmosEntityBuilder<TEntity>> configure) where TEntity : class
    {
        var builder = new CosmosEntityBuilder<TEntity>();
        configure(builder);
        _model.Add(builder.Build());
        return this;
    }

    /// <summary>
    ///     Declares a value conversion for every member of type <typeparamref name="TMember" />: documents store
    ///     <typeparamref name="TStored" />, entities keep <typeparamref name="TMember" />. Deliberately
    ///     <b>type-level</b> (unlike the relational per-member <c>Converts</c>): the SDK's LINQ translation
    ///     serializes a filter's constants by their type, so only a type-level converter keeps
    ///     <c>x =&gt; x.Status == Status.Active</c> comparing against the stored representation.
    /// </summary>
    /// <typeparam name="TMember">The CLR type on the entity.</typeparam>
    /// <typeparam name="TStored">The stored (JSON) type.</typeparam>
    /// <param name="toStored">Converts the CLR value to its stored representation.</param>
    /// <param name="fromStored">Converts the stored representation back.</param>
    public CosmosModelBuilder Converts<TMember, TStored>(Func<TMember, TStored> toStored, Func<TStored, TMember> fromStored)
    {
        _model.AddConverter(new CosmosValueConverter<TMember, TStored>(toStored, fromStored));
        return this;
    }

    /// <summary>
    ///     Builds the model. The DI extensions call this for you; call it directly when hosting without DI — the
    ///     built model feeds <see cref="CosmosClientFactory.Create(string, CosmosModel)" /> and <see cref="CosmosModel.Explain" />.
    /// </summary>
    public CosmosModel Build() => _model;

    private sealed class CosmosValueConverter<TMember, TStored>(Func<TMember, TStored> toStored, Func<TStored, TMember> fromStored)
        : System.Text.Json.Serialization.JsonConverter<TMember>
    {
        public override TMember Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) =>
            fromStored(System.Text.Json.JsonSerializer.Deserialize<TStored>(ref reader, options)!);

        public override void Write(System.Text.Json.Utf8JsonWriter writer, TMember value, System.Text.Json.JsonSerializerOptions options) =>
            System.Text.Json.JsonSerializer.Serialize(writer, toStored(value), options);
    }
}

/// <summary>Fluent configuration for one entity's Cosmos mapping.</summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public sealed class CosmosEntityBuilder<TEntity> where TEntity : class
{
    private string? _container;
    private string? _partitionKeyPath;
    private Func<TEntity, PartitionKey>? _partitionKey;
    private Func<TEntity, string>? _id;
    private Func<TEntity, string?>? _etag;
    private int? _ttlSeconds;
    private string? _idDescription;

    internal CosmosEntityBuilder()
    {
        // The eQuantic.Core.Data.Modeling annotations seed the builder; fluent calls override them
        // (conventions < annotations < fluent). Annotations outside the Cosmos vocabulary are ignored.
        if (Data.Modeling.EntityAttribute.NameFor(typeof(TEntity)) is { } name)
        {
            _container = name;
        }

        if (System.Attribute.GetCustomAttribute(typeof(TEntity), typeof(Data.Modeling.TimeToLiveAttribute))
            is Data.Modeling.TimeToLiveAttribute timeToLive)
        {
            _ttlSeconds = timeToLive.Seconds;
        }

        foreach (var property in typeof(TEntity).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (property.GetCustomAttributes(typeof(Data.Modeling.PartitionKeyAttribute), inherit: true).Length > 0)
            {
                _partitionKeyPath = "/" + CosmosNaming.StoredName(property);
                var partitionProperty = property;
                _partitionKey = entity => ToPartitionKey(partitionProperty.GetValue(entity));
            }

            if (property.GetCustomAttributes(typeof(Data.Modeling.EntityKeyAttribute), inherit: true).Length > 0)
            {
                var keyProperty = property;
                _id = entity => keyProperty.GetValue(entity)?.ToString()
                                ?? throw new InvalidOperationException($"'{typeof(TEntity).Name}' has a null id.");
                _idDescription = $"{property.Name} ([EntityKey])";
            }

            if (property.GetCustomAttributes(typeof(Data.Modeling.ConcurrencyTokenAttribute), inherit: true).Length > 0
                && property.PropertyType == typeof(string))
            {
                var etagProperty = property;
                _etag = entity => (string?)etagProperty.GetValue(entity);
            }
        }
    }

    /// <summary>Sets the container name (defaults to the entity type name).</summary>
    /// <param name="name">The container name.</param>
    public CosmosEntityBuilder<TEntity> Container(string name)
    {
        _container = name;
        return this;
    }

    /// <summary>
    ///     Declares the document id selector. Defaults to the member annotated <c>[EntityKey]</c>, or the entity's
    ///     <c>Id</c> property (which serializes to the Cosmos <c>id</c> field); override it when the key is exposed
    ///     differently.
    /// </summary>
    /// <typeparam name="TKey">The id member type.</typeparam>
    /// <param name="selector">The id selector.</param>
    public CosmosEntityBuilder<TEntity> Id<TKey>(Expression<Func<TEntity, TKey>> selector)
    {
        var read = selector.Compile();
        _id = entity => read(entity)?.ToString() ?? throw new InvalidOperationException($"'{typeof(TEntity).Name}' has a null id.");
        _idDescription = selector.GetMemberPath();
        return this;
    }

    /// <summary>
    ///     Declares the partition key from a member selector. The stored path defaults to the member name
    ///     (Cosmos preserves property names by default); pass <paramref name="path" /> to override it.
    /// </summary>
    /// <typeparam name="TKey">The partition key member type.</typeparam>
    /// <param name="selector">The member selector (e.g. <c>x =&gt; x.CountryCode</c>).</param>
    /// <param name="path">An explicit partition key path, or <c>null</c> to derive it from the member.</param>
    public CosmosEntityBuilder<TEntity> PartitionKey<TKey>(Expression<Func<TEntity, TKey>> selector, string? path = null)
    {
        _partitionKeyPath = path ?? "/" + string.Join("/", CosmosNaming.StoredPath(selector));
        var read = selector.Compile();
        _partitionKey = entity => ToPartitionKey(read(entity));
        return this;
    }

    /// <summary>Sets the container's default time-to-live.</summary>
    /// <param name="timeToLive">The default TTL.</param>
    public CosmosEntityBuilder<TEntity> TimeToLive(TimeSpan timeToLive)
    {
        _ttlSeconds = (int)timeToLive.TotalSeconds;
        return this;
    }

    /// <summary>
    ///     Declares the member holding the document's <c>_etag</c> (map it with
    ///     <c>[JsonPropertyName("_etag")]</c> so reads populate it). When the member carries a value, a
    ///     <c>Modify</c>/<c>Merge</c> stages a conditional replace (<c>If-Match</c>): a concurrent change makes the
    ///     commit fail with a <c>PreconditionFailed</c> <see cref="CosmosException" /> instead of silently winning.
    ///     Entities without a token (or with it unset) keep the unconditional last-write-wins upsert.
    /// </summary>
    /// <param name="selector">The concurrency token member selector (e.g. <c>x =&gt; x.ETag</c>).</param>
    public CosmosEntityBuilder<TEntity> ConcurrencyToken(Expression<Func<TEntity, string?>> selector)
    {
        _etag = selector.Compile();
        return this;
    }

    internal CosmosEntityConfiguration<TEntity> Build()
    {
        if (_partitionKey is null || _partitionKeyPath is null)
        {
            throw new InvalidOperationException(
                $"Entity '{typeof(TEntity).Name}' must declare a partition key (e.g. PartitionKey(x => x.CountryCode)).");
        }

        return new CosmosEntityConfiguration<TEntity>(
            _container ?? typeof(TEntity).Name, _partitionKeyPath, _partitionKey, _id ?? DefaultId(), _ttlSeconds, _etag,
            _idDescription);
    }

    private static Func<TEntity, string> DefaultId()
    {
        var property = typeof(TEntity).GetProperty("Id")
                       ?? throw new InvalidOperationException(
                           $"'{typeof(TEntity).Name}' has no 'Id' property; declare the id with Id(x => ...).");
        return entity => property.GetValue(entity)?.ToString()
                         ?? throw new InvalidOperationException($"'{typeof(TEntity).Name}' has a null id.");
    }

    private static PartitionKey ToPartitionKey<TKey>(TKey value) => value switch
    {
        null => Microsoft.Azure.Cosmos.PartitionKey.Null,
        string text => new Microsoft.Azure.Cosmos.PartitionKey(text),
        bool flag => new Microsoft.Azure.Cosmos.PartitionKey(flag),
        double number => new Microsoft.Azure.Cosmos.PartitionKey(number),
        _ => new Microsoft.Azure.Cosmos.PartitionKey(value.ToString()),
    };
}
