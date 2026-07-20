using System.Linq.Expressions;
using System.Reflection;
using eQuantic.Core.Data.Migration;
using eQuantic.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb.Migration;

/// <summary>
///     Applies provider-agnostic <see cref="MigrationOperation" />s to MongoDB through the driver: it creates
///     collections and indexes, converts and renames fields across existing documents, runs data updates and
///     hands the raw database to escape-hatch steps. Typed member selectors are resolved to their stored BSON
///     element names (honouring <c>[BsonElement]</c> / class maps), so a migration never spells a field as a string.
/// </summary>
public sealed class MongoMigrationExecutor : IMigrationExecutor
{
    private static readonly MethodInfo ApplyUpdateMethod =
        typeof(MongoMigrationExecutor).GetMethod(nameof(ApplyUpdateAsync), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly IMongoDatabase _database;
    private readonly Func<Type, string> _collectionName;
    private readonly MongoMigrationExecutionContext _context;

    /// <summary>Initializes the executor for a database.</summary>
    /// <param name="database">The target database.</param>
    /// <param name="collectionName">
    ///     Resolves an entity type to its collection name; defaults to the type name, matching the repository's
    ///     default (<c>MongoUnitOfWork.CollectionName</c>).
    /// </param>
    public MongoMigrationExecutor(IMongoDatabase database, Func<Type, string>? collectionName = null)
    {
        _database = database;
        _collectionName = collectionName ?? (type => type.Name);
        _context = new MongoMigrationExecutionContext(database);
    }

    /// <inheritdoc />
    public async Task ApplyAsync(IReadOnlyList<MigrationOperation> operations, CancellationToken cancellationToken = default)
    {
        foreach (var operation in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (operation)
            {
                case EnsureCollectionOperation ensure:
                    await EnsureCollectionAsync(ensure, cancellationToken).ConfigureAwait(false);
                    break;
                case EnsureIndexOperation index:
                    await EnsureIndexAsync(index, cancellationToken).ConfigureAwait(false);
                    break;
                case ConvertFieldOperation convert:
                    await ConvertFieldAsync(convert, cancellationToken).ConfigureAwait(false);
                    break;
                case RenameFieldOperation rename:
                    await RenameFieldAsync(rename, cancellationToken).ConfigureAwait(false);
                    break;
                case UpdateOperation update:
                    await ((Task)ApplyUpdateMethod.MakeGenericMethod(update.EntityType).Invoke(this, [update, cancellationToken])!)
                        .ConfigureAwait(false);
                    break;
                case RunOperation run:
                    await run.Action(_context, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported migration operation '{operation.GetType().Name}'.");
            }
        }
    }

    // -------------------------------------------------------------- operations

    private async Task EnsureCollectionAsync(EnsureCollectionOperation operation, CancellationToken cancellationToken)
    {
        var name = _collectionName(operation.EntityType);
        var options = new ListCollectionNamesOptions { Filter = Builders<BsonDocument>.Filter.Eq("name", name) };
        using var cursor = await _database.ListCollectionNamesAsync(options, cancellationToken).ConfigureAwait(false);
        var existing = await cursor.AnyAsync(cancellationToken).ConfigureAwait(false);
        if (!existing)
        {
            await _database.CreateCollectionAsync(name, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureIndexAsync(EnsureIndexOperation operation, CancellationToken cancellationToken)
    {
        var collection = Collection(operation.EntityType);

        var keys = new BsonDocument();
        foreach (var key in operation.Keys)
        {
            keys.Add(ResolveElementName(operation.EntityType, key.Selector), key.Descending ? -1 : 1);
        }

        var options = new CreateIndexOptions { Unique = operation.Unique };
        if (operation.ExpireAfter is { } expireAfter)
        {
            options.ExpireAfter = expireAfter;
        }

        if (operation.Name is { } name)
        {
            options.Name = name;
        }

        var model = new CreateIndexModel<BsonDocument>(keys, options);
        await collection.Indexes.CreateOneAsync(model, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task ConvertFieldAsync(ConvertFieldOperation operation, CancellationToken cancellationToken)
    {
        var collection = Collection(operation.EntityType);
        var field = ResolveElementName(operation.EntityType, operation.Field);

        var convert = new BsonDocument("$convert", new BsonDocument
        {
            { "input", "$" + field },
            { "to", ToConvertTypeName(operation.To) },
            { "onError", "$" + field },
            { "onNull", BsonNull.Value },
        });

        PipelineDefinition<BsonDocument, BsonDocument> pipeline = new BsonDocument[]
        {
            new("$set", new BsonDocument(field, convert)),
        };

        var update = new PipelineUpdateDefinition<BsonDocument>(pipeline);
        await collection.UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, update, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RenameFieldAsync(RenameFieldOperation operation, CancellationToken cancellationToken)
    {
        var collection = Collection(operation.EntityType);
        var field = ResolveElementName(operation.EntityType, operation.Field);
        var update = Builders<BsonDocument>.Update.Rename(field, operation.NewName);
        await collection.UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, update, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ApplyUpdateAsync<TEntity>(UpdateOperation operation, CancellationToken cancellationToken)
    {
        var collection = _database.GetCollection<TEntity>(_collectionName(typeof(TEntity)));
        var filter = (Expression<Func<TEntity, bool>>)operation.Predicate;

        var set = new BsonDocument();
        foreach (var assignment in operation.Sets)
        {
            set.Add(
                ResolveElementName(typeof(TEntity), assignment.Field),
                assignment.Value is null ? BsonNull.Value : BsonValue.Create(assignment.Value));
        }

        var update = new BsonDocumentUpdateDefinition<TEntity>(new BsonDocument("$set", set));
        await collection.UpdateManyAsync(filter, update, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // -------------------------------------------------------------- helpers

    private IMongoCollection<BsonDocument> Collection(Type entityType) =>
        _database.GetCollection<BsonDocument>(_collectionName(entityType));

    /// <summary>
    ///     Resolves a member selector to its stored BSON element path — the CLR path from the selector
    ///     (via <see cref="MemberPathExtensions.GetMemberPath" />), then each segment mapped to its BSON element
    ///     name through the registered class map so <c>[BsonElement]</c>/<c>[BsonId]</c> overrides are honoured.
    /// </summary>
    private static string ResolveElementName(Type entityType, LambdaExpression selector)
    {
        var parts = selector.GetMemberPath().Split('.');
        var names = new List<string>(parts.Length);
        var currentType = entityType;

        foreach (var part in parts)
        {
            var memberMap = BsonClassMap.LookupClassMap(currentType).AllMemberMaps
                .FirstOrDefault(map => map.MemberName == part);

            if (memberMap is not null)
            {
                names.Add(memberMap.ElementName);
                currentType = memberMap.MemberType;
            }
            else
            {
                names.Add(part);
                currentType = currentType.GetProperty(part)?.PropertyType
                              ?? currentType.GetField(part)?.FieldType
                              ?? typeof(object);
            }
        }

        return string.Join(".", names);
    }

    private static string ToConvertTypeName(MigrationFieldType type) => type switch
    {
        MigrationFieldType.String => "string",
        MigrationFieldType.Boolean => "bool",
        MigrationFieldType.Int32 => "int",
        MigrationFieldType.Int64 => "long",
        MigrationFieldType.Double => "double",
        MigrationFieldType.Decimal => "decimal",
        MigrationFieldType.DateTime => "date",
        MigrationFieldType.ObjectId => "objectId",
        _ => throw new NotSupportedException(
            $"Converting a field to '{type}' is not supported by the MongoDB $convert operator."),
    };
}
