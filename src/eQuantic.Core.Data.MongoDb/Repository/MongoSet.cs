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
    private readonly IQueryable<TEntity> _queryable;

    internal MongoSet(MongoUnitOfWork unitOfWork, IMongoCollection<TEntity> collection)
    {
        _unitOfWork = unitOfWork;
        _collection = collection;
        _queryable = collection.AsQueryable();
    }

    // ---------------------------------------------------------------- IQueryable
    public Type ElementType => _queryable.ElementType;
    public Expression Expression => _queryable.Expression;
    public IQueryProvider Provider => _queryable.Provider;
    public IEnumerator<TEntity> GetEnumerator() => _queryable.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _queryable.GetEnumerator();

    // ---------------------------------------------------------------- reads
    public IEnumerable<TEntity> Execute() => _queryable.ToList();

    public TEntity? Find<TKey>(TKey key) =>
        _collection.Find(Builders<TEntity>.Filter.Eq("_id", key)).FirstOrDefault();

    public async Task<TEntity?> FindAsync<TKey>(TKey key, CancellationToken cancellationToken = default) =>
        await (await _collection.FindAsync(Builders<TEntity>.Filter.Eq("_id", key), cancellationToken: cancellationToken)
            .ConfigureAwait(false)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

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

    public long UpdateMany(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateExpression) =>
        throw new NotSupportedException(
            "Set-based UpdateMany for MongoDB is not yet implemented — it needs a member-init expression → " +
            "$set translator (planned via eQuantic.Linq.Expressions). Load the documents and Modify them, or use DeleteMany.");

    public Task<long> UpdateManyAsync(Expression<Func<TEntity, bool>> filter,
        Expression<Func<TEntity, TEntity>> updateExpression, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Set-based UpdateMany for MongoDB is not yet implemented — it needs a member-init expression → " +
            "$set translator (planned via eQuantic.Linq.Expressions). Load the documents and Modify them, or use DeleteMany.");
}
