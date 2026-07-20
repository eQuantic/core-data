using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Nodes;
using eQuantic.Core.Data.Migration;
using eQuantic.Linq.Expressions;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;

namespace eQuantic.Core.Data.CosmosDb.Migration;

/// <summary>
///     Applies provider-agnostic <see cref="MigrationOperation" />s to Azure Cosmos DB through the SDK: it
///     creates containers (with their partition key and TTL), declares composite indexes, converts and renames
///     fields and runs data updates by querying the affected items and patching them, and hands the raw database
///     to escape-hatch steps. Typed member selectors are resolved to camelCase paths (matching the serializer).
/// </summary>
public sealed class CosmosMigrationExecutor : IMigrationExecutor
{
    private static readonly MethodInfo ApplyUpdateMethod =
        typeof(CosmosMigrationExecutor).GetMethod(nameof(ApplyUpdateAsync), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly Database _database;
    private readonly CosmosModel _model;
    private readonly CosmosMigrationExecutionContext _context;

    /// <summary>Initializes the executor.</summary>
    /// <param name="database">The target database.</param>
    /// <param name="model">The Cosmos model (container names, partition keys and ids per entity).</param>
    public CosmosMigrationExecutor(Database database, CosmosModel model)
    {
        _database = database;
        _model = model;
        _context = new CosmosMigrationExecutionContext(database);
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
                    await EnsureContainerAsync(ensure, cancellationToken).ConfigureAwait(false);
                    break;
                case EnsureIndexOperation index:
                    await EnsureCompositeIndexAsync(index, cancellationToken).ConfigureAwait(false);
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

    private async Task EnsureContainerAsync(EnsureCollectionOperation operation, CancellationToken cancellationToken)
    {
        var configuration = _model.For(operation.EntityType);
        var properties = new ContainerProperties(configuration.ContainerName, configuration.PartitionKeyPath);
        if (configuration.DefaultTimeToLiveSeconds is { } ttl)
        {
            properties.DefaultTimeToLive = ttl;
        }

        await _database.CreateContainerIfNotExistsAsync(properties, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureCompositeIndexAsync(EnsureIndexOperation operation, CancellationToken cancellationToken)
    {
        // Cosmos indexes every path by default, so a single-key index needs no declaration; only composite
        // indexes (used by multi-field ORDER BY) must be added to the container's indexing policy.
        if (operation.Keys.Count < 2)
        {
            return;
        }

        var container = _database.GetContainer(_model.For(operation.EntityType).ContainerName);
        var properties = (await container.ReadContainerAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Resource;

        var composite = new Collection<CompositePath>();
        foreach (var key in operation.Keys)
        {
            composite.Add(new CompositePath
            {
                Path = FieldPath(key.Selector),
                Order = key.Descending ? CompositePathSortOrder.Descending : CompositePathSortOrder.Ascending,
            });
        }

        var paths = composite.Select(path => (path.Path, path.Order)).ToList();
        var exists = properties.IndexingPolicy.CompositeIndexes.Any(existing =>
            existing.Select(path => (path.Path, path.Order)).SequenceEqual(paths));
        if (!exists)
        {
            properties.IndexingPolicy.CompositeIndexes.Add(composite);
            await container.ReplaceContainerAsync(properties, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ConvertFieldAsync(ConvertFieldOperation operation, CancellationToken cancellationToken)
    {
        var configuration = _model.For(operation.EntityType);
        var container = _database.GetContainer(configuration.ContainerName);
        var field = FieldElement(operation.Field);
        var partitionField = configuration.PartitionKeyPath.TrimStart('/');

        var query = new QueryDefinition(
            $"SELECT c[\"id\"], c[\"{partitionField}\"], c[\"{field}\"] FROM c WHERE {TypeCheck(operation.From)}(c[\"{field}\"])");

        await ForEachDocumentAsync(container, query, async (document, id, partitionKey) =>
        {
            var converted = ConvertScalar(document[field], operation.From, operation.To);
            await container.PatchItemAsync<JsonObject>(id, partitionKey,
                [PatchOperation.Set("/" + field, converted)], cancellationToken: cancellationToken).ConfigureAwait(false);
        }, partitionField, cancellationToken).ConfigureAwait(false);
    }

    private async Task RenameFieldAsync(RenameFieldOperation operation, CancellationToken cancellationToken)
    {
        var configuration = _model.For(operation.EntityType);
        var container = _database.GetContainer(configuration.ContainerName);
        var field = FieldElement(operation.Field);
        var partitionField = configuration.PartitionKeyPath.TrimStart('/');

        var query = new QueryDefinition(
            $"SELECT c[\"id\"], c[\"{partitionField}\"] FROM c WHERE IS_DEFINED(c[\"{field}\"])");

        await ForEachDocumentAsync(container, query, async (_, id, partitionKey) =>
        {
            await container.PatchItemAsync<JsonObject>(id, partitionKey,
                [PatchOperation.Move("/" + field, "/" + operation.NewName)], cancellationToken: cancellationToken).ConfigureAwait(false);
        }, partitionField, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyUpdateAsync<TEntity>(UpdateOperation operation, CancellationToken cancellationToken) where TEntity : class
    {
        var configuration = _model.For(typeof(TEntity));
        var container = _database.GetContainer(configuration.ContainerName);
        var predicate = (Expression<Func<TEntity, bool>>)operation.Predicate;
        var patch = operation.Sets
            .Select(assignment => PatchOperation.Set(FieldPath(assignment.Field), assignment.Value))
            .ToList();

        var items = new List<TEntity>();
        using var iterator = container.GetItemLinqQueryable<TEntity>().Where(predicate).ToFeedIterator();
        while (iterator.HasMoreResults)
        {
            items.AddRange(await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false));
        }

        await Task.WhenAll(items.Select(item => container.PatchItemAsync<TEntity>(
                configuration.GetId(item), configuration.GetPartitionKey(item), patch, cancellationToken: cancellationToken)))
            .ConfigureAwait(false);
    }

    // -------------------------------------------------------------- helpers

    private static async Task ForEachDocumentAsync(Container container, QueryDefinition query,
        Func<JsonObject, string, PartitionKey, Task> apply, string partitionField, CancellationToken cancellationToken)
    {
        using var iterator = container.GetItemQueryIterator<JsonObject>(query);
        while (iterator.HasMoreResults)
        {
            foreach (var document in await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = document["id"]!.GetValue<string>();
                await apply(document, id, ToPartitionKey(document[partitionField])).ConfigureAwait(false);
            }
        }
    }

    private static string FieldPath(LambdaExpression selector) =>
        "/" + string.Join("/", selector.GetMemberPath().Split('.').Select(CosmosNaming.CamelCase));

    private static string FieldElement(LambdaExpression selector) =>
        string.Join(".", selector.GetMemberPath().Split('.').Select(CosmosNaming.CamelCase));

    private static PartitionKey ToPartitionKey(JsonNode? node) => node?.GetValueKind() switch
    {
        null or System.Text.Json.JsonValueKind.Null => PartitionKey.Null,
        System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => new PartitionKey(node.GetValue<bool>()),
        System.Text.Json.JsonValueKind.Number => new PartitionKey(node.GetValue<double>()),
        _ => new PartitionKey(node!.GetValue<string>()),
    };

    private static string TypeCheck(MigrationFieldType type) => type switch
    {
        MigrationFieldType.String or MigrationFieldType.DateTime or MigrationFieldType.Guid or MigrationFieldType.ObjectId => "IS_STRING",
        MigrationFieldType.Boolean => "IS_BOOL",
        MigrationFieldType.Int32 or MigrationFieldType.Int64 or MigrationFieldType.Double or MigrationFieldType.Decimal => "IS_NUMBER",
        _ => throw new NotSupportedException($"Cannot test for the migration field type '{type}'."),
    };

    private static object? ConvertScalar(JsonNode? node, MigrationFieldType from, MigrationFieldType to)
    {
        if (node is null)
        {
            return null;
        }

        var text = from == MigrationFieldType.String
            ? node.GetValue<string>()
            : node.GetValue<double>().ToString(CultureInfo.InvariantCulture);

        return to switch
        {
            MigrationFieldType.String => text,
            MigrationFieldType.Boolean => Convert.ToBoolean(text, CultureInfo.InvariantCulture),
            MigrationFieldType.Int32 => Convert.ToInt32(text, CultureInfo.InvariantCulture),
            MigrationFieldType.Int64 => Convert.ToInt64(text, CultureInfo.InvariantCulture),
            MigrationFieldType.Double => Convert.ToDouble(text, CultureInfo.InvariantCulture),
            MigrationFieldType.Decimal => Convert.ToDecimal(text, CultureInfo.InvariantCulture),
            _ => throw new NotSupportedException($"Converting a field to '{to}' is not supported by the Cosmos migration executor."),
        };
    }
}
