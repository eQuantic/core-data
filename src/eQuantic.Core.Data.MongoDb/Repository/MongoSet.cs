using System.Collections;
using System.Linq.Expressions;
using eQuantic.Core.Data.Repository;
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
    public long DeleteMany(Expression<Func<TEntity, bool>> filter)
    {
        var session = _unitOfWork.Session;
        var result = session is null
            ? _collection.DeleteMany(filter)
            : _collection.DeleteMany(session, filter);
        return result.DeletedCount;
    }

    public async Task<long> DeleteManyAsync(Expression<Func<TEntity, bool>> filter, CancellationToken cancellationToken = default)
    {
        var session = _unitOfWork.Session;
        var result = session is null
            ? await _collection.DeleteManyAsync(filter, cancellationToken).ConfigureAwait(false)
            : await _collection.DeleteManyAsync(session, filter, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.DeletedCount;
    }

    public long UpdateMany(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateExpression)
    {
        var update = MongoUpdate.Build(updateExpression);
        var session = _unitOfWork.Session;
        var result = session is null
            ? _collection.UpdateMany(filter, update)
            : _collection.UpdateMany(session, filter, update);
        return result.ModifiedCount;
    }

    public async Task<long> UpdateManyAsync(Expression<Func<TEntity, bool>> filter,
        Expression<Func<TEntity, TEntity>> updateExpression, CancellationToken cancellationToken = default)
    {
        var update = MongoUpdate.Build(updateExpression);
        var session = _unitOfWork.Session;
        var result = session is null
            ? await _collection.UpdateManyAsync(filter, update, cancellationToken: cancellationToken).ConfigureAwait(false)
            : await _collection.UpdateManyAsync(session, filter, update, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.ModifiedCount;
    }
}
