using System.Collections;
using System.Linq.Expressions;
using eQuantic.Core.Data.Query;
using eQuantic.Core.Data.Repository;
using eQuantic.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

namespace eQuantic.Core.Data.MongoDb.Repository;

/// <summary>
///     A native MongoDB entity set. Reads project through the driver's LINQ provider
///     (<c>IMongoQueryable</c>); entity writes are staged on the <see cref="MongoUnitOfWork" /> and applied
///     on commit; set-based writes (<see cref="DeleteMany" />) run immediately, like the relational
///     providers' <c>ExecuteDelete</c>/<c>ExecuteUpdate</c>.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public sealed class MongoSet<TEntity> : Data.Repository.ISet<TEntity> where TEntity : class, IEntity
{
    private readonly MongoUnitOfWork _unitOfWork;
    private readonly IMongoCollection<TEntity> _collection;

    internal MongoSet(MongoUnitOfWork unitOfWork, IMongoCollection<TEntity> collection)
    {
        _unitOfWork = unitOfWork;
        _collection = collection;
    }

    // Reads join the active transaction session, so a query inside a transaction sees its own writes.
    private IQueryable<TEntity> Queryable =>
        _unitOfWork.Session is { } session ? _collection.AsQueryable(session) : _collection.AsQueryable();

    /// <summary>ANDs the global filter into a set-based write (a tenant-scoped delete stays tenant-scoped).</summary>
    private Expression<Func<TEntity, bool>> Scoped(Expression<Func<TEntity, bool>> filter) =>
        _unitOfWork.GlobalFilter<TEntity>() is { } global ? filter.AndAlso(global) : filter;

    /// <summary>
    ///     The filters a set-based write runs in turn, as the driver reads them: scoped, normalized, text without regard to
    ///     case as an escaped regular expression, and each test across a relation resolved — read first, in the session,
    ///     since a write's filter cannot hold a <c>$lookup</c>: one filter with the related keys as an <c>$in</c>, or,
    ///     past what an <c>$in</c> carries, batches of the matching documents' ids (see <see cref="MongoRelations" />).
    /// </summary>
    private Task<IReadOnlyList<FilterDefinition<TEntity>>> Ready(Expression<Func<TEntity, bool>> filter, CancellationToken cancellationToken) =>
        MongoPredicates.WriteFiltersAsync(Scoped(filter), _unitOfWork, _collection, cancellationToken);

    // ---------------------------------------------------------------- IQueryable
    public Type ElementType => Queryable.ElementType;
    public Expression Expression => Queryable.Expression;
    public IQueryProvider Provider => Queryable.Provider;
    public IEnumerator<TEntity> GetEnumerator() => Queryable.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => Queryable.GetEnumerator();

    // ---------------------------------------------------------------- reads
    public IEnumerable<TEntity> Execute() => Queryable.ToList();

    public TEntity? Find<TKey>(TKey key)
    {
        var filter = Builders<TEntity>.Filter.Eq("_id", key);
        var session = _unitOfWork.Session;
        return (session is null ? _collection.Find(filter) : _collection.Find(session, filter)).FirstOrDefault();
    }

    public async Task<TEntity?> FindAsync<TKey>(TKey key, CancellationToken cancellationToken = default)
    {
        var filter = Builders<TEntity>.Filter.Eq("_id", key);
        var session = _unitOfWork.Session;
        var cursor = session is null
            ? await _collection.FindAsync(filter, cancellationToken: cancellationToken).ConfigureAwait(false)
            : await _collection.FindAsync(session, filter, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await cursor.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- entity writes (staged → commit)
    public void Insert(TEntity item) => _unitOfWork.StageInsert(item);

    public Task InsertAsync(TEntity item, CancellationToken cancellationToken = default)
    {
        _unitOfWork.StageInsert(item);
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- set-based writes (immediate)
    public long DeleteMany(Expression<Func<TEntity, bool>> filter) => DeleteManyAsync(filter).GetAwaiter().GetResult();

    public async Task<long> DeleteManyAsync(Expression<Func<TEntity, bool>> filter, CancellationToken cancellationToken = default)
    {
        // A soft-delete entity's set-based delete stamps DeletedAt instead — the documents survive, scoped out of reads.
        if (EntityLifecycle.IsSoftDelete(typeof(TEntity), _unitOfWork.Conventions))
        {
            return await UpdateManyAsync(filter, EntityLifecycle.SoftDeleteUpdate<TEntity>(_unitOfWork.Conventions, _unitOfWork.Services), cancellationToken).ConfigureAwait(false);
        }

        var session = _unitOfWork.Session;
        long deleted = 0;
        foreach (var ready in await Ready(filter, cancellationToken).ConfigureAwait(false))
        {
            var result = session is null
                ? await _collection.DeleteManyAsync(ready, cancellationToken).ConfigureAwait(false)
                : await _collection.DeleteManyAsync(session, ready, cancellationToken: cancellationToken).ConfigureAwait(false);
            deleted += result.DeletedCount;
        }

        return deleted;
    }

    /// <remarks>
    ///     The count is the documents the filter <b>matched</b>, whether or not the update changed them: an update that
    ///     writes a document's own values back still matched it. Each document is updated only while it still matches
    ///     (MongoDB evaluates the filter again on a write conflict), so a filter on the key and other members is a
    ///     compare-and-set.
    /// </remarks>

    public long UpdateMany(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateExpression) =>
        UpdateManyAsync(filter, updateExpression).GetAwaiter().GetResult();

    public async Task<long> UpdateManyAsync(Expression<Func<TEntity, bool>> filter,
        Expression<Func<TEntity, TEntity>> updateExpression, CancellationToken cancellationToken = default)
    {
        var update = WithUpdateStamp(MongoUpdate.Build(updateExpression), updateExpression);
        var session = _unitOfWork.Session;
        long matched = 0;
        foreach (var ready in await Ready(filter, cancellationToken).ConfigureAwait(false))
        {
            var result = await MongoDuplicateKeys.TranslatingAsync(async () => session is null
                ? await _collection.UpdateManyAsync(ready, update, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await _collection.UpdateManyAsync(session, ready, update, cancellationToken: cancellationToken).ConfigureAwait(false))
                .ConfigureAwait(false);
            matched += result.MatchedCount;
        }

        return matched;
    }

    // ---------------------------------------------------------------- returning updates (immediate)

    /// <summary>
    ///     Applies the update as <see cref="UpdateManyAsync" /> does and returns the documents it wrote, as stored after
    ///     it: the matching ids, then one <c>findOneAndUpdate</c> per document, filtered on the id <b>and</b> the
    ///     filter (a document that stopped matching is left alone) and returning the document after the update. Each
    ///     document's update and read is one atomic step; the set is one only inside an explicit transaction.
    /// </summary>
    internal async Task<IReadOnlyList<TEntity>> UpdateAndGetAsync(Expression<Func<TEntity, bool>> filter,
        Expression<Func<TEntity, TEntity>> updateExpression, CancellationToken cancellationToken)
    {
        var update = WithUpdateStamp(MongoUpdate.Build(updateExpression), updateExpression);
        var session = _unitOfWork.Session;
        var options = new FindOneAndUpdateOptions<TEntity> { ReturnDocument = ReturnDocument.After };
        var updated = new List<TEntity>();
        foreach (var scoped in await Ready(filter, cancellationToken).ConfigureAwait(false))
        {
            var ids = await (session is null ? _collection.Find(scoped) : _collection.Find(session, scoped))
                .Project(Builders<TEntity>.Projection.Include("_id"))
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            foreach (var id in ids)
            {
                var one = Builders<TEntity>.Filter.And(new BsonDocumentFilterDefinition<TEntity>(new BsonDocument("_id", id["_id"])), scoped);
                var document = await MongoDuplicateKeys.TranslatingAsync(() => session is null
                    ? _collection.FindOneAndUpdateAsync(one, update, options, cancellationToken)
                    : _collection.FindOneAndUpdateAsync(session, one, update, options, cancellationToken)).ConfigureAwait(false);
                if (document is not null)
                {
                    updated.Add(document);
                }
            }
        }

        return updated;
    }

    // ---------------------------------------------------------------- upserts (immediate)

    private const int UpsertAttempts = 25;

    /// <summary>
    ///     Inserts the entity, or updates the document matching it on the target members, and returns the document as
    ///     stored. The update is a <c>findOneAndUpdate</c> of the target returning the document after it; when nothing
    ///     matches, an upsert with <c>$setOnInsert</c> inserts the entity — and when it finds the target after all
    ///     (another upsert inserted it in between) the update runs again. A unique index on the target makes the race
    ///     safe: a concurrent second insert fails with E11000 and becomes the update. Without one, MongoDB itself can
    ///     insert twice.
    /// </summary>
    internal async Task<TEntity> UpsertAsync(TEntity entity, IReadOnlyList<string>? on,
        Expression<Func<TEntity, TEntity>>? updateExpression, Expression<Func<TEntity, bool>>? conflictFilter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var classMap = BsonClassMap.LookupClassMap(typeof(TEntity));
        var idMember = classMap.IdMemberMap
                       ?? throw new InvalidOperationException($"'{typeof(TEntity).Name}' has no mapped id member to upsert by.");
        var target = (on ?? [idMember.MemberName]).Select(member => typeof(TEntity).GetProperty(member)
                                                                    ?? throw new NotSupportedException(
                                                                        $"'{typeof(TEntity).Name}' has no member '{member}' to upsert on."))
            .ToList();

        // An entity whose id is unset is a new document: it takes the id the driver generates on insert (an ObjectId, a
        // string represented as one, a Guid), so that no upsert filters on an empty id, nor stores one. An upsert on the
        // id needs it to filter by; one on other members only when it inserts.
        var targetsId = target.Any(member => member.Name == idMember.MemberName);
        if (targetsId)
        {
            MongoTakenKeys.AssignIds(_collection, _collection.DocumentSerializer, [entity]);
        }

        // The target: the entity's value of each member, and the conflict filter.
        var parameter = Expression.Parameter(typeof(TEntity), "x");
        Expression body = Expression.Constant(true);
        foreach (var member in target)
        {
            var equal = Expression.Equal(Expression.Property(parameter, member),
                Expression.Constant(member.GetValue(entity), member.PropertyType));
            body = body is ConstantExpression ? equal : Expression.AndAlso(body, equal);
        }

        var targetFilter = Expression.Lambda<Func<TEntity, bool>>(body, parameter);
        var filter = Builders<TEntity>.Filter.Where(conflictFilter is null ? targetFilter : targetFilter.AndAlso(conflictFilter));

        EntityLifecycle.StampForInsert(entity, _unitOfWork.Conventions, _unitOfWork.Services);
        MongoUnitOfWork.StampInitialVersion(entity);
        var update = UpsertUpdate(entity, classMap, target, updateExpression);

        var session = _unitOfWork.Session;
        var options = new FindOneAndUpdateOptions<TEntity> { ReturnDocument = ReturnDocument.After };
        BsonDocument? inserting = null;
        Exception? lostInsert = null;
        for (var attempt = 1; ; attempt++)
        {
            var existing = await MongoDuplicateKeys.TranslatingAsync(() => session is null
                ? _collection.FindOneAndUpdateAsync(filter, update, options, cancellationToken)
                : _collection.FindOneAndUpdateAsync(session, filter, update, options, cancellationToken)).ConfigureAwait(false);
            if (existing is not null)
            {
                return existing;
            }

            if (lostInsert is not null)
            {
                // The insert lost to a unique index that is not the target: no retry can take it.
                throw MongoDuplicateKeys.From(lostInsert)!;
            }

            if (inserting is null)
            {
                // The store's values for the insert, once: a retry that ends in the update leaves a gap, never a repeat.
                await _unitOfWork.AssignStoreGeneratedAsync([(entity, typeof(TEntity))], cancellationToken).ConfigureAwait(false);
                if (!targetsId)
                {
                    MongoTakenKeys.AssignIds(_collection, _collection.DocumentSerializer, [entity]);
                }

                inserting = entity.ToBsonDocument(typeof(TEntity));
                if (on is null)
                {
                    // The target is the _id, which the upsert takes from the filter.
                    inserting.Remove("_id");
                }
            }

            try
            {
                var insert = new BsonDocument("$setOnInsert", inserting);
                var result = session is null
                    ? await _collection.UpdateOneAsync(filter, insert, new UpdateOptions { IsUpsert = true }, cancellationToken).ConfigureAwait(false)
                    : await _collection.UpdateOneAsync(session, filter, insert, new UpdateOptions { IsUpsert = true }, cancellationToken).ConfigureAwait(false);
                if (result.UpsertedId is not null)
                {
                    // What was inserted is exactly the entity: the document as stored.
                    return entity;
                }
            }
            catch (Exception exception) when (MongoDuplicateKeys.From(exception) is { } duplicate)
            {
                if (attempt >= UpsertAttempts || session is { IsInTransaction: true })
                {
                    // Inside a transaction MongoDB has already aborted it on the error: there is nothing to retry in.
                    throw duplicate;
                }

                // A concurrent upsert inserted the target first (or another unique key is taken: the next read says which).
                lostInsert = exception;
            }
        }
    }

    /// <summary>
    ///     The update an upsert applies to the existing document: the update expression, or a <c>$set</c> of every
    ///     element but the <c>_id</c>, the target, the store-generated members, the token and the creation stamps; then
    ///     the update's stamps unless the expression assigns them, and the token's <c>$inc</c>.
    /// </summary>
    private UpdateDefinition<TEntity> UpsertUpdate(TEntity entity, BsonClassMap classMap,
        IReadOnlyList<System.Reflection.PropertyInfo> target, Expression<Func<TEntity, TEntity>>? updateExpression)
    {
        var token = MongoModeling.ConcurrencyMember(typeof(TEntity));
        var generated = MongoModeling.StoreGenerated(typeof(TEntity));
        var creation = EntityLifecycle.CreationMembers(typeof(TEntity), _unitOfWork.Conventions);
        var updateStamps = EntityLifecycle.UpdateStamps(typeof(TEntity), _unitOfWork.Conventions, _unitOfWork.Services);
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var definitions = new List<UpdateDefinition<TEntity>>();

        if (updateExpression is not null)
        {
            foreach (var assignment in UpdateInterpreter.Interpret(updateExpression))
            {
                if (assignment.Name == classMap.IdMemberMap?.MemberName || assignment.Name == token?.Name
                    || generated.Any(member => member.Property.Name == assignment.Name))
                {
                    throw new NotSupportedException(
                        $"An upsert's update cannot assign '{assignment.Name}': the _id identifies the document, the store " +
                        "assigns a store-generated member, and the engine bumps the concurrency token.");
                }

                assigned.Add(assignment.Name);
            }

            definitions.Add(MongoUpdate.Build(updateExpression));
        }
        else
        {
            var set = new BsonDocument();
            foreach (var map in classMap.AllMemberMaps)
            {
                if (map == classMap.IdMemberMap || map.MemberName == token?.Name || creation.Contains(map.MemberName)
                    || updateStamps.Any(stamp => stamp.Name == map.MemberName)
                    || target.Any(member => member.Name == map.MemberName)
                    || generated.Any(member => member.Property.Name == map.MemberName))
                {
                    continue;
                }

                set[map.ElementName] = MongoFieldNames.Serialize(typeof(TEntity), map.MemberInfo, map.Getter(entity));
                assigned.Add(map.MemberName);
            }

            if (set.ElementCount > 0)
            {
                definitions.Add(new BsonDocument("$set", set));
            }
        }

        var stamps = new BsonDocument();
        foreach (var stamp in updateStamps)
        {
            if (updateExpression is not null && assigned.Contains(stamp.Name))
            {
                continue;
            }

            stamps[MongoFieldNames.Resolve(typeof(TEntity), stamp.Member)] =
                MongoFieldNames.Serialize(typeof(TEntity), stamp.Member, stamp.Value);
        }

        if (stamps.ElementCount > 0)
        {
            definitions.Add(new BsonDocument("$set", stamps));
        }

        if (token is not null)
        {
            definitions.Add(new BsonDocument("$inc", new BsonDocument(MongoFieldNames.Resolve(typeof(TEntity), token), 1)));
        }

        if (definitions.Count == 0)
        {
            // Nothing to write beyond what identifies the document: the update still matches, so the document comes back.
            var first = target[0];
            definitions.Add(new BsonDocument("$set", new BsonDocument(MongoFieldNames.Resolve(typeof(TEntity), first),
                MongoFieldNames.Serialize(typeof(TEntity), first, first.GetValue(entity)))));
        }

        return Builders<TEntity>.Update.Combine(definitions);
    }

    // ---------------------------------------------------------------- inserts skipping duplicates (immediate)

    /// <summary>
    ///     Inserts the batch at once and counts the documents inserted: an ordered <c>insertMany</c>, which stops at a
    ///     taken key and fails with <see cref="DuplicateKeyException" /> (the documents before it stay written, unless an
    ///     explicit transaction is open); with <paramref name="skipDuplicates" />, an unordered one that writes every
    ///     other document and discards only the duplicate-key errors. Inside an explicit transaction, where MongoDB
    ///     aborts the transaction on any write error, the duplicates are found first, inside the session
    ///     (<see cref="MongoTakenKeys" />: the <c>_id</c> and every unique index), and only the free documents are
    ///     inserted; a key a concurrent writer takes in between surfaces as MongoDB's transient write conflict.
    /// </summary>
    internal async Task<long> InsertManyAsync(IEnumerable<TEntity> items, bool skipDuplicates, CancellationToken cancellationToken)
    {
        var entities = items as IReadOnlyList<TEntity> ?? items.ToList();
        if (entities.Count == 0)
        {
            return 0;
        }

        foreach (var entity in entities)
        {
            EntityLifecycle.StampForInsert(entity, _unitOfWork.Conventions, _unitOfWork.Services);
            MongoUnitOfWork.StampInitialVersion(entity);
        }

        await _unitOfWork.AssignStoreGeneratedAsync(entities.Select(entity => ((object)entity, typeof(TEntity))).ToList(),
            cancellationToken).ConfigureAwait(false);

        var options = new InsertManyOptions { IsOrdered = !skipDuplicates };
        var session = _unitOfWork.Session;
        if (skipDuplicates && session is { IsInTransaction: true })
        {
            // A write error would abort the transaction: leave out what is taken before writing anything.
            entities = await MongoTakenKeys.FreeAsync(_collection, session, entities, cancellationToken).ConfigureAwait(false);
            if (entities.Count == 0)
            {
                return 0;
            }

            options.IsOrdered = true;
        }

        try
        {
            if (session is null)
            {
                await _collection.InsertManyAsync(entities, options, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _collection.InsertManyAsync(session, entities, options, cancellationToken).ConfigureAwait(false);
            }

            return entities.Count;
        }
        catch (MongoBulkWriteException exception) when (skipDuplicates && session is not { IsInTransaction: true }
                                                         && exception.WriteErrors.Count > 0
                                                         && exception.WriteErrors.All(MongoDuplicateKeys.IsDuplicate)
                                                         && exception.WriteConcernError is null)
        {
            return entities.Count - exception.WriteErrors.Count;
        }
        catch (Exception exception) when (skipDuplicates && session is { IsInTransaction: true }
                                          && MongoDuplicateKeys.From(exception) is { } duplicate)
        {
            // The keys were free when they were read inside the session; one was taken by the time of the insert.
            throw new DuplicateKeyException(
                "The store refused the write: a key or a unique key it holds was taken after the batch was checked inside the " +
                "transaction, and MongoDB aborted the transaction on it (it aborts on any write error).",
                duplicate.ConstraintName, exception);
        }
        catch (Exception exception) when (MongoDuplicateKeys.From(exception) is { } duplicate)
        {
            throw duplicate;
        }
    }

    /// <summary>Stamps <c>UpdatedAt</c> into a set-based update when the entity tracks it and the caller did not assign it.</summary>
    private UpdateDefinition<TEntity> WithUpdateStamp(UpdateDefinition<TEntity> update,
        Expression<Func<TEntity, TEntity>> updateExpression)
    {
        if (EntityLifecycle.UpdateStamp(typeof(TEntity), _unitOfWork.Conventions) is not { } stamp
            || (updateExpression.Body is MemberInitExpression init
                && init.Bindings.Any(binding => binding.Member.Name == stamp.Name)))
        {
            return update;
        }

        var parameter = Expression.Parameter(typeof(TEntity), "x");
        var selector = Expression.Lambda<Func<TEntity, DateTime?>>(
            Expression.Property(parameter, stamp.Name), parameter);
        return Builders<TEntity>.Update.Combine(update, Builders<TEntity>.Update.Set(selector, (DateTime?)(DateTime)stamp.Value!));
    }
}
