using System.Linq.Expressions;
using eQuantic.Core.Data.Repository;
using eQuantic.Linq.Specification;

namespace eQuantic.Core.Data.Cassandra.Repository;

/// <summary>
///     The native Apache Cassandra repository — the read surface from <see cref="CassandraReadRepository{TEntity, TKey}" />
///     plus writes. Entity writes are staged on the unit of work (a Cassandra <c>INSERT</c> is an upsert, so
///     <c>Add</c>/<c>Modify</c>/<c>Merge</c> map to it) and flushed on commit; set-based <c>DeleteMany</c>/<c>UpdateMany</c>
///     run immediately and require the primary key in their <c>WHERE</c>.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TKey">The key type.</typeparam>
public class CassandraRepository<TEntity, TKey> :
    CassandraReadRepository<TEntity, TKey>,
    IRepository<TEntity, TKey>,
    IAsyncRepository<TEntity, TKey>,
    IQueryableRepository<TEntity, TKey>,
    IAsyncQueryableRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
{
    /// <summary>Initializes the repository over a unit of work.</summary>
    /// <param name="unitOfWork">The queryable unit of work (a <see cref="CassandraUnitOfWork" />).</param>
    public CassandraRepository(IQueryableUnitOfWork unitOfWork) : base(unitOfWork)
    {
    }

    private CassandraSet<TEntity> Set() => new(UnitOfWork, Session);

    // ---------------------------------------------------------------- staged entity writes (flushed on commit)

    /// <inheritdoc />
    public void Add(TEntity item) => UnitOfWork.StageUpsert(item);

    /// <inheritdoc />
    public void AddRange(IEnumerable<TEntity> items)
    {
        foreach (var item in items)
        {
            UnitOfWork.StageUpsert(item);
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
        UnitOfWork.StageUpsert(item);
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
    public long DeleteMany(Expression<Func<TEntity, bool>> filter) => Set().DeleteMany(filter);

    /// <inheritdoc />
    public long DeleteMany(ISpecification<TEntity> specification) => Set().DeleteMany(specification.SatisfiedBy());

    /// <inheritdoc />
    public Task<long> DeleteManyAsync(Expression<Func<TEntity, bool>> filter, CancellationToken cancellationToken = default) =>
        Set().DeleteManyAsync(filter, cancellationToken);

    /// <inheritdoc />
    public Task<long> DeleteManyAsync(ISpecification<TEntity> specification, CancellationToken cancellationToken = default) =>
        Set().DeleteManyAsync(specification.SatisfiedBy(), cancellationToken);

    /// <inheritdoc />
    public long UpdateMany(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateFactory) =>
        Set().UpdateMany(filter, updateFactory);

    /// <inheritdoc />
    public long UpdateMany(ISpecification<TEntity> specification, Expression<Func<TEntity, TEntity>> updateFactory) =>
        Set().UpdateMany(specification.SatisfiedBy(), updateFactory);

    /// <inheritdoc />
    public Task<long> UpdateManyAsync(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateFactory, CancellationToken cancellationToken = default) =>
        Set().UpdateManyAsync(filter, updateFactory, cancellationToken);

    /// <inheritdoc />
    public Task<long> UpdateManyAsync(ISpecification<TEntity> specification, Expression<Func<TEntity, TEntity>> updateFactory, CancellationToken cancellationToken = default) =>
        Set().UpdateManyAsync(specification.SatisfiedBy(), updateFactory, cancellationToken);
}
