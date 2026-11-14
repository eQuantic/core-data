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

    /// <summary>
    ///     A set-based write's filter as the SDK translates it: scoped, in the shapes the SDK reads, and each test across a
    ///     relation resolved into the related keys (read first: a relation crosses containers, and a patch's condition
    ///     reads only the item) — the condition each item is then written under.
    /// </summary>
    private Task<Expression<Func<TEntity, bool>>> Ready(Expression<Func<TEntity, bool>> filter, CancellationToken cancellationToken) =>
        CosmosPredicates.ReadyAsync(Scoped(filter), _unitOfWork, cancellationToken);

    // ---------------------------------------------------------------- set-based writes (immediate)
    public long DeleteMany(Expression<Func<TEntity, bool>> filter) => DeleteManyAsync(filter).GetAwaiter().GetResult();

    /// <remarks>
    ///     Cosmos DB has no delete by query: the matching items are read, and each is deleted under the <c>_etag</c> of a
    ///     read that still finds it matching the filter — a changed item is read again, and one that stopped matching is
    ///     left alone. At most 32 items are worked on at once. The count is the items
    ///     deleted. A soft-delete entity's delete stamps <c>DeletedAt</c> instead.
    /// </remarks>
    public async Task<long> DeleteManyAsync(Expression<Func<TEntity, bool>> filter, CancellationToken cancellationToken = default)
    {
        if (EntityLifecycle.IsSoftDelete(typeof(TEntity), _unitOfWork.Conventions))
        {
            return await UpdateManyAsync(filter, EntityLifecycle.SoftDeleteUpdate<TEntity>(_unitOfWork.Conventions, _unitOfWork.Services),
                cancellationToken).ConfigureAwait(false);
        }

        var scoped = await Ready(filter, cancellationToken).ConfigureAwait(false);
        var where = SqlFilter.Of(_queryable.Where(scoped));
        var matches = await MaterializeAsync(_queryable.Where(scoped), cancellationToken).ConfigureAwait(false);
        var deleted = await ForEachItemAsync(matches, item => DeleteMatchingAsync(item, where, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        return deleted.Sum();
    }

    /// <summary>Deletes an item under the <c>_etag</c> of a read that still finds it matching; 1 when it did, 0 when not.</summary>
    private async Task<int> DeleteMatchingAsync(TEntity item, SqlFilter where, CancellationToken cancellationToken)
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
    }

    /// <summary>
    ///     How many items a set-based write works on at once. Each item is a read and a write of its own (Cosmos DB has
    ///     no write by query), and all the matches at once — tens of thousands of requests — would be throttled (429)
    ///     past the SDK's retries, leaving a delete or an update half done.
    /// </summary>
    internal const int MaxConcurrentItems = 32;

    /// <summary>
    ///     Runs a write per item, at most <see cref="MaxConcurrentItems" /> at once, and answers each one's result in the
    ///     items' order. The first failure stops the items not yet started and is thrown once those running have finished
    ///     (they are not cut off mid-flight).
    /// </summary>
    private static async Task<TResult[]> ForEachItemAsync<TResult>(IReadOnlyList<TEntity> items, Func<TEntity, Task<TResult>> write,
        CancellationToken cancellationToken)
    {
        var results = new TResult[items.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, items.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentItems, CancellationToken = cancellationToken },
            async (index, _) => results[index] = await write(items[index]).ConfigureAwait(false)).ConfigureAwait(false);
        return results;
    }

    public long UpdateMany(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateExpression) =>
        UpdateManyAsync(filter, updateExpression).GetAwaiter().GetResult();

    /// <remarks>
    ///     Cosmos DB has no update by query: the matching items are read, and each is patched <b>under the filter</b>
    ///     (<c>PatchItemRequestOptions.FilterPredicate</c>), so the server applies the patch only while the item still
    ///     matches — a filter on the key and other members is a compare-and-set. The count is the patches applied,
    ///     whether or not they changed the values. An update of more than ten paths patches each item several times in
    ///     one transactional batch, the first patch under the filter: the item takes all of it or none. At most 32 items
    ///     are patched at once.
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
        var scoped = await Ready(filter, cancellationToken).ConfigureAwait(false);

        // A text test that ignores case is never a patch's condition: the store's condition evaluator answers it with 412
        // (the vNext emulator does), which reads as "no longer matches" and patches nothing, silently. Nor is a filter the
        // SDK translates with a JOIN (a test over an embedded array), which the evaluator refuses. Such a filter patches
        // each item under the _etag of a read that finds it matching.
        var where = SqlFilter.Of(_queryable.Where(scoped));
        var condition = CosmosPredicates.TestsTextIgnoringCase(scoped) || !where.OverRootAlone ? null : "FROM root WHERE " + where.Predicate;
        var matches = await MaterializeAsync(_queryable.Where(scoped), cancellationToken).ConfigureAwait(false);
        var patched = await ForEachItemAsync(matches, item => PatchMatchingAsync(item, patch, condition, where, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return patched.Where(item => item is not null).Select(item => item!).ToList();
    }

    /// <summary>
    ///     Patches an item while it still matches the filter, and answers it as the patch left it, or <c>null</c> when it no
    ///     longer matched (or went away): under the filter as the patch's condition, or, without a <paramref name="condition" />
    ///     (a filter the condition evaluator cannot be trusted with), under the <c>_etag</c> of a read that finds it matching. A
    ///     unique key the patch would take is a <see cref="DuplicateKeyException" />, by either path.
    /// </summary>
    private async Task<TEntity?> PatchMatchingAsync(TEntity item, IReadOnlyList<PatchOperation> patch, string? condition, SqlFilter where,
        CancellationToken cancellationToken)
    {
        var id = _configuration.GetId(item);
        var partitionKey = _configuration.GetPartitionKey(item);
        try
        {
            try
            {
                if (condition is not null)
                {
                    return await CosmosPatch.ApplyAsync<TEntity>(_container, id, partitionKey, patch, condition, ifMatchEtag: null,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (CosmosException exception) when (exception.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                // The store could not evaluate the filter as a patch's condition (a function its condition evaluator does
                // not take): the item is patched under the _etag of a read that still finds it matching, below.
            }

            return await PatchUnderETagAsync(id, partitionKey, patch, where, cancellationToken).ConfigureAwait(false);
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
    }

    /// <summary>
    ///     Patches an item under the <c>_etag</c> of a read that finds it still matching the filter, reading again after a
    ///     concurrent change: the same compare-and-set as a patch under the filter, in two round trips.
    /// </summary>
    private async Task<TEntity?> PatchUnderETagAsync(string id, PartitionKey partitionKey, IReadOnlyList<PatchOperation> patch, SqlFilter where,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < ConditionalAttempts; attempt++)
        {
            if (await MatchingETagAsync(id, partitionKey, where, cancellationToken).ConfigureAwait(false) is not { } etag)
            {
                return null;
            }

            try
            {
                return await CosmosPatch.ApplyAsync<TEntity>(_container, id, partitionKey, patch, filterPredicate: null, etag, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CosmosException exception) when (exception.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
            {
                // Changed since the read: read it again.
            }
            catch (CosmosException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        throw new InvalidOperationException(
            $"The item '{id}' kept changing while it was being updated; {ConditionalAttempts} attempts did not settle.");
    }

    /// <summary>
    ///     The patch, with the update's lifecycle stamps unless it assigns them. Any number of paths: beyond the ten
    ///     Cosmos DB takes in one patch, <see cref="CosmosPatch.ApplyAsync{TEntity}" /> splits it within one batch.
    /// </summary>
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

        return operations;
    }

    // ---------------------------------------------------------------- upserts (immediate)

    private const int ConditionalAttempts = 25;

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
                        return await CosmosPatch.ApplyAsync<TEntity>(_container, id, partitionKey, patch, filterPredicate: null,
                            stored.ETag, cancellationToken).ConfigureAwait(false);
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
                    await _unitOfWork.AssignStoreGeneratedAsync([(entity, _configuration, partitionKey)], new CosmosServerClock(), cancellationToken)
                        .ConfigureAwait(false);
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
            .Where(conflictFilter is null ? target : CosmosPredicates.Rendered(target.AndAlso(conflictFilter), _unitOfWork));
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
    private async Task<string?> MatchingETagAsync(string id, PartitionKey partitionKey, SqlFilter where, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition($"SELECT VALUE root._etag {where.From} WHERE root.id = @eqId AND ({where.Predicate})")
            .WithParameter("@eqId", id);
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
    ///     A filter as the SDK's LINQ translation renders it over the alias <c>root</c>, split at the query's own
    ///     <c>WHERE</c>: what comes before it (<c>FROM root</c>, and the <c>JOIN</c>s the SDK writes for a test over an
    ///     embedded array, whose aliases the predicate reads) and the predicate. The condition a patch is applied under,
    ///     and the one a delete re-checks.
    /// </summary>
    private readonly record struct SqlFilter(string From, string Predicate)
    {
        private const string Select = "SELECT VALUE root ";
        private const string Where = " WHERE ";

        /// <summary>Whether the predicate reads the document alone, with no alias a <c>JOIN</c> introduced.</summary>
        public bool OverRootAlone => From == "FROM root";

        public static SqlFilter Of(IQueryable<TEntity> query)
        {
            var text = query.ToQueryDefinition().QueryText;
            if (!text.StartsWith(Select, StringComparison.Ordinal))
            {
                throw new NotSupportedException($"The SDK translated the filter as '{text}', which a set-based write does not recognise.");
            }

            // The query's own WHERE is the first outside every parenthesis (a JOIN's subquery holds WHEREs of its own)
            // and outside every string literal (a value may hold the word).
            var depth = 0;
            for (var index = Select.Length; index < text.Length; index++)
            {
                switch (text[index])
                {
                    case '"' or '\'':
                        index = ClosingQuote(text, index);
                        break;
                    case '(':
                        depth++;
                        break;
                    case ')':
                        depth--;
                        break;
                    case ' ' when depth == 0 && string.CompareOrdinal(text, index, Where, 0, Where.Length) == 0:
                        return new SqlFilter(text[Select.Length..index], text[(index + Where.Length)..]);
                }
            }

            return new SqlFilter(text[Select.Length..], "true");
        }

        private static int ClosingQuote(string text, int opening)
        {
            for (var index = opening + 1; index < text.Length; index++)
            {
                if (text[index] == '\\')
                {
                    index++;
                }
                else if (text[index] == text[opening])
                {
                    return index;
                }
            }

            return text.Length;
        }
    }

    // ---------------------------------------------------------------- inserts skipping duplicates (immediate)

    /// <summary>
    ///     Creates every entity, at most <see cref="MaxConcurrentItems" /> at once, and counts the creates that applied. A taken id (or unique key)
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
            // One read of the server's clock for this call's inserts, taken now: never one an earlier write read.
            await _unitOfWork.AssignStoreGeneratedAsync(inserts, new CosmosServerClock(), cancellationToken).ConfigureAwait(false);
        }

        // A few creates at a time, as a set-based write's items are: all of a large batch at once would be throttled.
        var conflicts = new System.Collections.Concurrent.ConcurrentBag<CosmosException>();
        var created = await ForEachItemAsync(entities, async entity =>
        {
            try
            {
                await _container.CreateItemAsync(entity, _configuration.GetPartitionKey(entity), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return 1;
            }
            catch (CosmosException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                conflicts.Add(exception);
                return 0;
            }
        }, cancellationToken).ConfigureAwait(false);

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
