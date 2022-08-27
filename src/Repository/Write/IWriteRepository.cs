using System;
using System.Linq.Expressions;
using eQuantic.Linq.Specification;

namespace eQuantic.Core.Data.Repository.Write;

/// <summary>
/// The write repository
/// </summary>
/// <typeparam name="TUnitOfWork">The type of the unit of work.</typeparam>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <seealso cref="eQuantic.Core.Data.Repository.Write.IWriteRepository{TEntity, TKey}" />
/// <seealso cref="eQuantic.Core.Data.Repository.IRepository{TUnitOfWork}" />
public interface IWriteRepository<TUnitOfWork, TEntity, TKey> : IWriteRepository<TEntity, TKey>, IRepository<TUnitOfWork>
    where TUnitOfWork : IUnitOfWork
    where TEntity : class, IEntity, new()
{
}

/// <summary>
/// The write repository
/// </summary>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <seealso cref="eQuantic.Core.Data.Repository.Write.IWriteRepository{TEntity, TKey}" />
/// <seealso cref="eQuantic.Core.Data.Repository.IRepository{TUnitOfWork}" />
public interface IWriteRepository<TEntity, TKey> : IRepository
    where TEntity : class, IEntity, new()
{
    /// <summary>
    /// Add item into repository
    /// </summary>
    /// <param name="item">Item to add to repository</param>
    void Add(TEntity item);

    /// <summary>
    /// Delete filtered elements of type TEntity in repository
    /// </summary>
    /// <param name="filter"></param>
    /// <returns></returns>
    long DeleteMany(Expression<Func<TEntity, bool>> filter);

    /// <summary>
    /// Delete specified elements of type TEntity in repository
    /// </summary>
    /// <param name="specification"></param>
    /// <returns></returns>
    long DeleteMany(ISpecification<TEntity> specification);

    /// <summary>
    /// Sets modified entity into the repository. When calling Commit() method in UnitOfWork
    /// these changes will be saved into the storage
    /// </summary>
    /// <param name="persisted">The persisted item</param>
    /// <param name="current">The current item</param>
    void Merge(TEntity persisted, TEntity current);

    /// <summary>
    /// Set item as modified
    /// </summary>
    /// <param name="item">Item to modify</param>
    void Modify(TEntity item);

    /// <summary>
    /// Delete item
    /// </summary>
    /// <param name="item">Item to delete</param>
    void Remove(TEntity item);

    /// <summary>
    ///Track entity into this repository, really in UnitOfWork.
    ///In EF this can be done with Attach and with Update in NH
    /// </summary>
    /// <param name="item">Item to attach</param>
    void TrackItem(TEntity item);

    /// <summary>
    /// Update filtered elements of type TEntity in repository
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="updateFactory"></param>
    /// <returns></returns>
    long UpdateMany(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateFactory);

    /// <summary>
    /// Update specified elements of type TEntity in repository
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="updateFactory"></param>
    /// <returns></returns>
    long UpdateMany(ISpecification<TEntity> specification, Expression<Func<TEntity, TEntity>> updateFactory);
}