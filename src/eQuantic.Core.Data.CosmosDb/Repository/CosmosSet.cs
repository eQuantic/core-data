using System.Collections;
using System.Linq.Expressions;
using eQuantic.Core.Data.Query;
using eQuantic.Core.Data.Repository;
using eQuantic.Linq.Expressions;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;

namespace eQuantic.Core.Data.CosmosDb.Repository;

/// <summary>
///     A native Azure Cosmos DB entity set. Reads project through the SDK's LINQ provider; entity writes are
///     staged on the <see cref="CosmosUnitOfWork" /> and applied on commit; set-based writes
///     (<see cref="DeleteMany" />/<see cref="UpdateMany" />) query the matching items and then delete or patch
///     each one (Cosmos has no server-side delete/update-by-query).
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public sealed class CosmosSet<TEntity> : Data.Repository.ISet<TEntity> where TEntity : class, IEntity
{
    private readonly CosmosUnitOfWork _unitOfWork;
    private readonly Container _container;
    private readonly CosmosEntityConfiguration _configuration;
    private readonly IQueryable<TEntity> _queryable;

    internal CosmosSet(CosmosUnitOfWork unitOfWork, Container container)
    {
        _unitOfWork = unitOfWork;
        _container = container;
        _configuration = unitOfWork.Configuration<TEntity>();
        _queryable = container.GetItemLinqQueryable<TEntity>(allowSynchronousQueryExecution: true);
    }

    // ---------------------------------------------------------------- IQueryable
    public Type ElementType => _queryable.ElementType;
    public Expression Expression => _queryable.Expression;
    public IQueryProvider Provider => _queryable.Provider;
    public IEnumerator<TEntity> GetEnumerator() => _queryable.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _queryable.GetEnumerator();

    // ---------------------------------------------------------------- reads
    public IEnumerable<TEntity> Execute() => _queryable.ToList();

    public TEntity? Find<TKey>(TKey key) => FindAsync(key).GetAwaiter().GetResult();

    public async Task<TEntity?> FindAsync<TKey>(TKey key, CancellationToken cancellationToken = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.id = @id").WithParameter("@id", key?.ToString());
        using var iterator = _container.GetItemQueryIterator<TEntity>(query);
        while (iterator.HasMoreResults)
        {
            foreach (var item in await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                return item;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- entity writes (staged → commit)
    public void Insert(TEntity item) => _unitOfWork.StageInsert(item);

    public Task InsertAsync(TEntity item, CancellationToken cancellationToken = default)
    {
        _unitOfWork.StageInsert(item);
        return Task.CompletedTask;
    }

    /// <summary>ANDs the global filter into a set-based write (a tenant-scoped delete stays tenant-scoped).</summary>
    private Expression<Func<TEntity, bool>> Scoped(Expression<Func<TEntity, bool>> filter) =>
        _unitOfWork.GlobalFilter<TEntity>() is { } global ? filter.AndAlso(global) : filter;

    // ---------------------------------------------------------------- set-based writes (immediate)
    public long DeleteMany(Expression<Func<TEntity, bool>> filter) => DeleteManyAsync(filter).GetAwaiter().GetResult();

    /// <remarks>
    ///     Cosmos DB has no delete by query: the matching items are read, and each is deleted under the <c>_etag</c> of a
    ///     read that still finds it matching the filter — a changed item is read again, and one that stopped matching is
    ///     left alone. The count is the items deleted. A soft-delete entity's delete stamps <c>DeletedAt</c> instead.
    /// </remarks>
    public async Task<long> DeleteManyAsync(Expression<Func<TEntity, bool>> filter, CancellationToken cancellationToken = default)
    {
        if (EntityLifecycle.IsSoftDelete(typeof(TEntity), _unitOfWork.Conventions))
        {
            return await UpdateManyAsync(filter, EntityLifecycle.SoftDeleteUpdate<TEntity>(_unitOfWork.Conventions, _unitOfWork.Services),
                cancellationToken).ConfigureAwait(false);
        }

        var scoped = Scoped(filter);
        var where = WhereClause(scoped);
        var matches = await MaterializeAsync(_queryable.Where(scoped), cancellationToken).ConfigureAwait(false);
        var deleted = await Task.WhenAll(matches.Select(async item =>
        {
            var id = _configuration.GetId(item);
            var partitionKey = _configuration.GetPartitionKey(item);
            for (var attempt = 0; attempt < ConditionalAttempts; attempt++)
            {
                if (await MatchingETagAsync(id, partitionKey, where, cancellationToken).ConfigureAwait(false) is not { } etag)
                {
                    return 0;
                }

                using var response = await _container.DeleteItemStreamAsync(id, partitionKey,
                    new ItemRequestOptions { IfMatchEtag = etag }, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return 1;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return 0;
                }

                if (response.StatusCode != System.Net.HttpStatusCode.PreconditionFailed)
                {
                    response.EnsureSuccessStatusCode();
                }
            }

            throw new InvalidOperationException(
                $"The item '{id}' kept changing while it was being deleted; {ConditionalAttempts} attempts did not settle.");
        })).ConfigureAwait(false);
        return deleted.Sum();
    }

    public long UpdateMany(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateExpression) =>
        UpdateManyAsync(filter, updateExpression).GetAwaiter().GetResult();

    /// <remarks>
    ///     Cosmos DB has no update by query: the matching items are read, and each is patched <b>under the filter</b>
    ///     (<c>PatchItemRequestOptions.FilterPredicate</c>), so the server applies the patch only while the item still
    ///     matches — a filter on the key and other members is a compare-and-set. The count is the patches applied,
    ///     whether or not they changed the values.
    /// </remarks>
    public async Task<long> UpdateManyAsync(Expression<Func<TEntity, bool>> filter,
        Expression<Func<TEntity, TEntity>> updateExpression, CancellationToken cancellationToken = default) =>
        (await PatchMatchingAsync(filter, updateExpression, cancellationToken).ConfigureAwait(false)).Count;

    /// <summary>
    ///     Applies the update as <see cref="UpdateManyAsync" /> does and returns the items it patched, as the patch left
    ///     them (each patch answers with its item).
    /// </summary>
    internal Task<IReadOnlyList<TEntity>> UpdateAndGetAsync(Expression<Func<TEntity, bool>> filter,
        Expression<Func<TEntity, TEntity>> updateExpression, CancellationToken cancellationToken) =>
        PatchMatchingAsync(filter, updateExpression, cancellationToken);

    private async Task<IReadOnlyList<TEntity>> PatchMatchingAsync(Expression<Func<TEntity, bool>> filter,
        Expression<Func<TEntity, TEntity>> updateExpression, CancellationToken cancellationToken)
    {
        var patch = WithUpdateStamps(CosmosPatch.Build(updateExpression), updateExpression);
        var scoped = Scoped(filter);
        var options = new PatchItemRequestOptions { FilterPredicate = "FROM root WHERE " + WhereClause(scoped) };
        var matches = await MaterializeAsync(_queryable.Where(scoped), cancellationToken).ConfigureAwait(false);
        var patched = await Task.WhenAll(matches.Select(async item =>
        {
            try
            {
                var response = await _container.PatchItemAsync<TEntity>(_configuration.GetId(item), _configuration.GetPartitionKey(item),
                    patch, options, cancellationToken).ConfigureAwait(false);
                return response.Resource;
            }
            catch (CosmosException exception) when (exception.StatusCode is System.Net.HttpStatusCode.PreconditionFailed
                                                        or System.Net.HttpStatusCode.NotFound)
            {
                // The item stopped matching the filter (or went away) since it was read: the patch did not apply.
                return null;
            }
            catch (CosmosException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                throw new DuplicateKeyException(null, exception);
            }
        })).ConfigureAwait(false);
        return patched.Where(item => item is not null).Select(item => item!).ToList();
    }

    /// <summary>The patch, with the update's lifecycle stamps unless it assigns them; Cosmos DB patches ten paths at most.</summary>
    private IReadOnlyList<PatchOperation> WithUpdateStamps(IReadOnlyList<PatchOperation> patch,
        Expression<Func<TEntity, TEntity>>? updateExpression)
    {
        var assigned = updateExpression is null
            ? []
            : UpdateInterpreter.Interpret(updateExpression).Select(assignment => assignment.Name).ToHashSet(StringComparer.Ordinal);
        var operations = patch.ToList();
        foreach (var stamp in EntityLifecycle.UpdateStamps(typeof(TEntity), _unitOfWork.Conventions, _unitOfWork.Services))
        {
            if (!assigned.Contains(stamp.Name))
            {
                operations.Add(PatchOperation.Set("/" + CosmosNaming.StoredName(stamp.Member), stamp.Value));
            }
        }

        return operations.Count <= MaxPatchOperations
            ? operations
            : throw new NotSupportedException(
                $"Cosmos DB patches at most {MaxPatchOperations} paths in one operation, and this update needs {operations.Count} " +
                "(the update's lifecycle stamps included); assign fewer members.");
    }

    // ---------------------------------------------------------------- upserts (immediate)

    private const int ConditionalAttempts = 25;
    private const int MaxPatchOperations = 10;

    /// <summary>
    ///     Inserts the entity, or updates the item matching it on the target members, guarded by the item's <c>_etag</c>,
    ///     and returns the item as stored. The target is read (by its id, or by a query inside the entity's partition);
    ///     an existing item is replaced (or patched with the update expression) under the <c>_etag</c> it was read with,
    ///     and a missing one is created — a lost race (412 on the replace, 409 on the create) reads the target again.
    ///     The id is unique within a partition, and so is a container's unique key: a target other than those is not
    ///     held unique, and two concurrent upserts of it can both create.
    /// </summary>
    internal async Task<TEntity> UpsertAsync(TEntity entity, IReadOnlyList<string>? on,
        Expression<Func<TEntity, TEntity>>? updateExpression, Expression<Func<TEntity, bool>>? conflictFilter,
        Action<TEntity, TEntity> takeKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        EntityLifecycle.StampForInsert(entity, _unitOfWork.Conventions, _unitOfWork.Services);
        var partitionKey = _configuration.GetPartitionKey(entity);
        var targetQuery = on is null ? null : TargetQuery(entity, on, conflictFilter, partitionKey);
        var patch = updateExpression is null ? null : WithUpdateStamps(CosmosPatch.Build(updateExpression), updateExpression);

        var assigned = false;
        CosmosException? lostCreate = null;
        for (var attempt = 1; ; attempt++)
        {
            var existing = targetQuery is null
                ? await ReadAsync(_configuration.GetId(entity), partitionKey, cancellationToken).ConfigureAwait(false)
                : await QueryTargetAsync(targetQuery, partitionKey, cancellationToken).ConfigureAwait(false);

            try
            {
                if (existing is { } stored)
                {
                    var id = _configuration.GetId(stored.Resource);
                    if (patch is not null)
                    {
                        var patched = await _container.PatchItemAsync<TEntity>(id, partitionKey, patch,
                            new PatchItemRequestOptions { IfMatchEtag = stored.ETag }, cancellationToken).ConfigureAwait(false);
                        return patched.Resource;
                    }

                    var replaced = await _container.ReplaceItemAsync(Replacement(entity, stored.Resource, takeKey), id, partitionKey,
                        new ItemRequestOptions { IfMatchEtag = stored.ETag }, cancellationToken).ConfigureAwait(false);
                    return replaced.Resource;
                }

                if (lostCreate is not null)
                {
                    // The create lost to a key that is not the target (another unique key): no retry can take it.
                    throw new DuplicateKeyException(null, lostCreate);
                }

                if (!assigned)
                {
                    // The store's values for the insert, once: a retry that ends in the update leaves a gap, never a repeat.
                    await _unitOfWork.AssignStoreGeneratedAsync([(entity, _configuration, partitionKey)], cancellationToken).ConfigureAwait(false);
                    assigned = true;
                }

                var created = await _container.CreateItemAsync(entity, partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);
                return created.Resource;
            }
            catch (CosmosException exception) when (attempt < ConditionalAttempts
                                                    && exception.StatusCode is System.Net.HttpStatusCode.PreconditionFailed
                                                        or System.Net.HttpStatusCode.NotFound)
            {
                // Another writer changed (or removed) the target between the read and the write: read it again.
            }
            catch (CosmosException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                if (existing is not null || attempt >= ConditionalAttempts)
                {
                    // An update that would take another item's unique key.
                    throw new DuplicateKeyException(null, exception);
                }

                // A concurrent upsert created the target first (or another key is taken: the next read says which).
                lostCreate = exception;
            }
        }
    }

    /// <summary>
    ///     The document an upsert's update writes: the entity, under the stored item's key, keeping its creation stamps and
    ///     the values the store assigned, with the update's stamps. A copy: the caller's entity is left as it is.
    /// </summary>
    private TEntity Replacement(TEntity entity, TEntity stored, Action<TEntity, TEntity> takeKey)
    {
        var options = CosmosEntitySerializer.BuildOptions(_unitOfWork.EntityModel);
        var copy = System.Text.Json.JsonSerializer.Deserialize<TEntity>(System.Text.Json.JsonSerializer.Serialize(entity, options), options)!;
        takeKey(copy, stored);
        foreach (var name in EntityLifecycle.CreationMembers(typeof(TEntity), _unitOfWork.Conventions))
        {
            if (typeof(TEntity).GetProperty(name) is { CanWrite: true } property)
            {
                property.SetValue(copy, property.GetValue(stored));
            }
        }

        foreach (var member in _configuration.StoreGenerated)
        {
            member.Property.SetValue(copy, member.Property.GetValue(stored));
        }

        foreach (var stamp in EntityLifecycle.UpdateStamps(typeof(TEntity), _unitOfWork.Conventions, _unitOfWork.Services))
        {
            if (stamp.Member is System.Reflection.PropertyInfo { CanWrite: true } property)
            {
                property.SetValue(copy, stamp.Value);
            }
        }

        return copy;
    }

    private IQueryable<TEntity> TargetQuery(TEntity entity, IReadOnlyList<string> on,
        Expression<Func<TEntity, bool>>? conflictFilter, PartitionKey partitionKey)
    {
        var parameter = Expression.Parameter(typeof(TEntity), "x");
        Expression? body = null;
        foreach (var name in on)
        {
            var member = typeof(TEntity).GetProperty(name)
                         ?? throw new NotSupportedException($"'{typeof(TEntity).Name}' has no member '{name}' to upsert on.");
            var equal = Expression.Equal(Expression.Property(parameter, member), Expression.Constant(member.GetValue(entity), member.PropertyType));
            body = body is null ? equal : Expression.AndAlso(body, equal);
        }

        var target = Expression.Lambda<Func<TEntity, bool>>(body!, parameter);
        return _container.GetItemLinqQueryable<TEntity>(requestOptions: new QueryRequestOptions { PartitionKey = partitionKey })
            .Where(conflictFilter is null ? target : target.AndAlso(conflictFilter));
    }

    private async Task<(TEntity Resource, string ETag)?> QueryTargetAsync(IQueryable<TEntity> query, PartitionKey partitionKey,
        CancellationToken cancellationToken)
    {
        var found = (await MaterializeAsync(query.Take(1), cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        return found is null
            ? null
            : await ReadAsync(_configuration.GetId(found), partitionKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(TEntity Resource, string ETag)?> ReadAsync(string id, PartitionKey partitionKey, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _container.ReadItemAsync<TEntity>(id, partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);
            return (response.Resource, response.ETag);
        }
        catch (CosmosException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>The <c>_etag</c> of the item when it still matches the filter, or <c>null</c>.</summary>
    private async Task<string?> MatchingETagAsync(string id, PartitionKey partitionKey, string where, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition($"SELECT VALUE root._etag FROM root WHERE root.id = @eqId AND ({where})").WithParameter("@eqId", id);
        using var iterator = _container.GetItemQueryIterator<string>(query, requestOptions: new QueryRequestOptions { PartitionKey = partitionKey });
        while (iterator.HasMoreResults)
        {
            foreach (var etag in await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                return etag;
            }
        }

        return null;
    }

    /// <summary>
    ///     The predicate the SDK's LINQ translation renders for the filter, over the alias <c>root</c> — the condition a
    ///     patch is applied under, and the one a delete re-checks.
    /// </summary>
    private string WhereClause(Expression<Func<TEntity, bool>> filter)
    {
        var text = _queryable.Where(filter).ToQueryDefinition().QueryText;
        var where = text.IndexOf(" WHERE ", StringComparison.Ordinal);
        return where < 0 ? "true" : text[(where + " WHERE ".Length)..];
    }

    // ---------------------------------------------------------------- inserts skipping duplicates (immediate)

    /// <summary>
    ///     Creates every entity at once, concurrently, and counts the creates that applied. A taken id (or unique key)
    ///     answers 409: with <paramref name="skipDuplicates" /> the entity is left out; without it the call fails with
    ///     <see cref="DuplicateKeyException" /> once every create has answered — each create is its own write, so the
    ///     others stay written.
    /// </summary>
    internal async Task<long> InsertManyAsync(IEnumerable<TEntity> items, bool skipDuplicates, CancellationToken cancellationToken)
    {
        var entities = items as IReadOnlyList<TEntity> ?? items.ToList();
        if (entities.Count == 0)
        {
            return 0;
        }

        var inserts = new List<(object Entity, CosmosEntityConfiguration Configuration, PartitionKey PartitionKey)>(entities.Count);
        foreach (var entity in entities)
        {
            EntityLifecycle.StampForInsert(entity, _unitOfWork.Conventions, _unitOfWork.Services);
            inserts.Add((entity, _configuration, _configuration.GetPartitionKey(entity)));
        }

        if (_configuration.StoreGenerated.Count > 0)
        {
            await _unitOfWork.AssignStoreGeneratedAsync(inserts, cancellationToken).ConfigureAwait(false);
        }

        var conflicts = new System.Collections.Concurrent.ConcurrentBag<CosmosException>();
        var created = await Task.WhenAll(inserts.Select(async insert =>
        {
            try
            {
                await _container.CreateItemAsync((TEntity)insert.Entity, insert.PartitionKey, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return 1;
            }
            catch (CosmosException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                conflicts.Add(exception);
                return 0;
            }
        })).ConfigureAwait(false);

        if (!skipDuplicates && conflicts.TryPeek(out var conflict))
        {
            throw new DuplicateKeyException(null, conflict);
        }

        return created.Sum();
    }

    private static async Task<List<TEntity>> MaterializeAsync(IQueryable<TEntity> query, CancellationToken cancellationToken)
    {
        var results = new List<TEntity>();
        using var iterator = query.ToFeedIterator();
        while (iterator.HasMoreResults)
        {
            results.AddRange(await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false));
        }

        return results;
    }
}
