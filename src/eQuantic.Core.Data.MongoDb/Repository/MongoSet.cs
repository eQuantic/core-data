using System.Collections;
using System.Linq.Expressions;
using eQuantic.Core.Data.Repository;
using eQuantic.Linq.Expressions;
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
        var result = session is null
            ? await _collection.DeleteManyAsync(Scoped(filter), cancellationToken).ConfigureAwait(false)
            : await _collection.DeleteManyAsync(session, Scoped(filter), cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.DeletedCount;
    }

    public long UpdateMany(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateExpression) =>
        UpdateManyAsync(filter, updateExpression).GetAwaiter().GetResult();

    public async Task<long> UpdateManyAsync(Expression<Func<TEntity, bool>> filter,
        Expression<Func<TEntity, TEntity>> updateExpression, CancellationToken cancellationToken = default)
    {
        var update = WithUpdateStamp(MongoUpdate.Build(updateExpression), updateExpression);
        var session = _unitOfWork.Session;
        var result = session is null
            ? await _collection.UpdateManyAsync(Scoped(filter), update, cancellationToken: cancellationToken).ConfigureAwait(false)
            : await _collection.UpdateManyAsync(session, Scoped(filter), update, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.ModifiedCount;
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
