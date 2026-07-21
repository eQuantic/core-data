using System.Linq.Expressions;
using eQuantic.Core.Data.Repository;
using eQuantic.Linq.Specification;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb.Repository;

/// <summary>
///     The native MongoDB repository — the read surface from <see cref="MongoReadRepository{TEntity, TKey}" />
///     plus writes. Entity writes (<c>Add</c>/<c>Modify</c>/<c>Remove</c>/<c>Merge</c>) are staged on the unit
///     of work and flushed as one bulk write on commit; set-based writes (<c>DeleteMany</c>/<c>UpdateMany</c>)
///     run immediately against the collection, honouring an open transaction.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TKey">The key type.</typeparam>
public class MongoRepository<TEntity, TKey> :
    MongoReadRepository<TEntity, TKey>,
    IRepository<TEntity, TKey>,
    IAsyncRepository<TEntity, TKey>,
    IQueryableRepository<TEntity, TKey>,
    IAsyncQueryableRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
{
    /// <summary>Initializes the repository over a unit of work.</summary>
    /// <param name="unitOfWork">The queryable unit of work (a <see cref="MongoUnitOfWork" />).</param>
    public MongoRepository(IQueryableUnitOfWork unitOfWork) : base(unitOfWork)
    {
    }

    // ---------------------------------------------------------------- staged entity writes (flushed on commit)

    /// <inheritdoc />
    public void Add(TEntity item) => UnitOfWork.StageInsert(item);

    /// <inheritdoc />
    public void AddRange(IEnumerable<TEntity> items)
    {
        foreach (var item in items)
        {
            UnitOfWork.StageInsert(item);
        }
    }

    /// <inheritdoc />
    public void Modify(TEntity item) => UnitOfWork.SetModified(item);

    /// <inheritdoc />
    public void Merge(TEntity persisted, TEntity current) => UnitOfWork.ApplyCurrentValues(persisted, current);

    /// <inheritdoc />
    public void Remove(TEntity item) => UnitOfWork.StageDelete(item);

    /// <inheritdoc />
    public void TrackItem(TEntity item) => UnitOfWork.Attach(item);

    /// <inheritdoc />
    public Task AddAsync(TEntity item, CancellationToken cancellationToken = default)
    {
        UnitOfWork.StageInsert(item);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task AddRangeAsync(IEnumerable<TEntity> items, CancellationToken cancellationToken = default)
    {
        AddRange(items);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MergeAsync(TEntity persisted, TEntity current)
    {
        UnitOfWork.ApplyCurrentValues(persisted, current);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ModifyAsync(TEntity item)
    {
        UnitOfWork.SetModified(item);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(TEntity item)
    {
        UnitOfWork.StageDelete(item);
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- immediate set-based writes

    /// <inheritdoc />
    public long DeleteMany(Expression<Func<TEntity, bool>> filter)
    {
        var session = UnitOfWork.Session;
        var result = session is null
            ? Collection.DeleteMany(filter)
            : Collection.DeleteMany(session, filter);
        return result.DeletedCount;
    }

    /// <inheritdoc />
    public long DeleteMany(ISpecification<TEntity> specification) => DeleteMany(specification.SatisfiedBy());

    /// <inheritdoc />
    public async Task<long> DeleteManyAsync(Expression<Func<TEntity, bool>> filter, CancellationToken cancellationToken = default)
    {
        var session = UnitOfWork.Session;
        var result = session is null
            ? await Collection.DeleteManyAsync(filter, cancellationToken).ConfigureAwait(false)
            : await Collection.DeleteManyAsync(session, filter, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.DeletedCount;
    }

    /// <inheritdoc />
    public Task<long> DeleteManyAsync(ISpecification<TEntity> specification, CancellationToken cancellationToken = default) =>
        DeleteManyAsync(specification.SatisfiedBy(), cancellationToken);

    /// <inheritdoc />
    public long UpdateMany(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateFactory)
    {
        var update = MongoUpdate.Build(updateFactory);
        var session = UnitOfWork.Session;
        var result = session is null
            ? Collection.UpdateMany(filter, update)
            : Collection.UpdateMany(session, filter, update);
        return result.ModifiedCount;
    }

    /// <inheritdoc />
    public long UpdateMany(ISpecification<TEntity> specification, Expression<Func<TEntity, TEntity>> updateFactory) =>
        UpdateMany(specification.SatisfiedBy(), updateFactory);

    /// <inheritdoc />
    public async Task<long> UpdateManyAsync(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateFactory, CancellationToken cancellationToken = default)
    {
        var update = MongoUpdate.Build(updateFactory);
        var session = UnitOfWork.Session;
        var result = session is null
            ? await Collection.UpdateManyAsync(filter, update, cancellationToken: cancellationToken).ConfigureAwait(false)
            : await Collection.UpdateManyAsync(session, filter, update, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.ModifiedCount;
    }

    /// <inheritdoc />
    public Task<long> UpdateManyAsync(ISpecification<TEntity> specification, Expression<Func<TEntity, TEntity>> updateFactory, CancellationToken cancellationToken = default) =>
        UpdateManyAsync(specification.SatisfiedBy(), updateFactory, cancellationToken);
}
