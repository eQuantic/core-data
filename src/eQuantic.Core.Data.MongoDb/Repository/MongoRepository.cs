using System.Linq.Expressions;
using eQuantic.Core.Data.Query;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Write;
using eQuantic.Linq.Specification;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb.Repository;

/// <summary>
///     The native MongoDB repository — the read surface from <see cref="MongoReadRepository{TEntity, TKey}" />
///     plus writes. Entity writes (<c>Add</c>/<c>Modify</c>/<c>Remove</c>/<c>Merge</c>) are staged on the unit
///     of work and flushed as one bulk write on commit; set-based writes (<c>DeleteMany</c>/<c>UpdateMany</c>, through
///     the <see cref="MongoSet{TEntity}" />: scoped by the global filters, soft-deleting and stamping as the lifecycle
///     conventions say, counting what they matched) run immediately against the collection, honouring an open
///     transaction. Upserts, inserts skipping duplicates and returning updates run immediately too.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TKey">The key type.</typeparam>
public class MongoRepository<TEntity, TKey> :
    MongoReadRepository<TEntity, TKey>,
    IRepository<TEntity, TKey>,
    IAsyncRepository<TEntity, TKey>,
    IQueryableRepository<TEntity, TKey>,
    IAsyncQueryableRepository<TEntity, TKey>,
    IUpsertRepository<TEntity, TKey>,
    IReturningUpdateRepository<TEntity, TKey>,
    IPartialModifyRepository<TEntity, TKey>
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

    private MongoSet<TEntity> Set() => new(UnitOfWork, Collection);

    /// <inheritdoc />
    public long DeleteMany(Expression<Func<TEntity, bool>> filter) => Set().DeleteMany(filter);

    /// <inheritdoc />
    public long DeleteMany(ISpecification<TEntity> specification) => DeleteMany(specification.SatisfiedBy());

    /// <inheritdoc />
    public Task<long> DeleteManyAsync(Expression<Func<TEntity, bool>> filter, CancellationToken cancellationToken = default) =>
        Set().DeleteManyAsync(filter, cancellationToken);

    /// <inheritdoc />
    public Task<long> DeleteManyAsync(ISpecification<TEntity> specification, CancellationToken cancellationToken = default) =>
        DeleteManyAsync(specification.SatisfiedBy(), cancellationToken);

    /// <inheritdoc />
    public long UpdateMany(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateFactory) =>
        Set().UpdateMany(filter, updateFactory);

    /// <inheritdoc />
    public long UpdateMany(ISpecification<TEntity> specification, Expression<Func<TEntity, TEntity>> updateFactory) =>
        UpdateMany(specification.SatisfiedBy(), updateFactory);

    /// <inheritdoc />
    public Task<long> UpdateManyAsync(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateFactory, CancellationToken cancellationToken = default) =>
        Set().UpdateManyAsync(filter, updateFactory, cancellationToken);

    /// <inheritdoc />
    public Task<long> UpdateManyAsync(ISpecification<TEntity> specification, Expression<Func<TEntity, TEntity>> updateFactory, CancellationToken cancellationToken = default) =>
        UpdateManyAsync(specification.SatisfiedBy(), updateFactory, cancellationToken);

    // ---------------------------------------------------------------- upserts and returning updates (immediate)

    /// <inheritdoc />
    public Task<TEntity> UpsertAsync(TEntity entity, CancellationToken cancellationToken = default) =>
        Set().UpsertAsync(entity, null, null, null, cancellationToken);

    /// <inheritdoc />
    public Task<TEntity> UpsertAsync(TEntity entity, Expression<Func<TEntity, object?>> on,
        Expression<Func<TEntity, TEntity>>? update = null, Expression<Func<TEntity, bool>>? conflictFilter = null,
        CancellationToken cancellationToken = default) =>
        Set().UpsertAsync(entity, MemberSelection.Names(on), update, conflictFilter, cancellationToken);

    /// <inheritdoc />
    public Task<long> InsertManyAsync(IEnumerable<TEntity> items, bool skipDuplicates = false,
        CancellationToken cancellationToken = default) =>
        Set().InsertManyAsync(items, skipDuplicates, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<TEntity>> UpdateAndGetAsync(Expression<Func<TEntity, bool>> filter,
        Expression<Func<TEntity, TEntity>> update, CancellationToken cancellationToken = default) =>
        Set().UpdateAndGetAsync(filter, update, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<TEntity>> UpdateAndGetAsync(ISpecification<TEntity> specification,
        Expression<Func<TEntity, TEntity>> update, CancellationToken cancellationToken = default) =>
        Set().UpdateAndGetAsync(specification.SatisfiedBy(), update, cancellationToken);

    // ---------------------------------------------------------------- member-scoped staged updates

    /// <inheritdoc />
    public void Modify(TEntity item, Expression<Func<TEntity, object?>> members) =>
        UnitOfWork.StagePartialUpdate(item, MemberSelection.Names(members));

    /// <inheritdoc />
    public Task ModifyAsync(TEntity item, Expression<Func<TEntity, object?>> members)
    {
        UnitOfWork.StagePartialUpdate(item, MemberSelection.Names(members));
        return Task.CompletedTask;
    }
}
