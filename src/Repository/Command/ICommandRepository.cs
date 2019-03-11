using eQuantic.Core.Linq.Specification;
using System;
using System.Linq.Expressions;

namespace eQuantic.Core.Data.Repository.Command
{
    public interface ICommandRepository<TEntity, TKey> : IRepository
        where TEntity : class, IEntity, new()
    {
        /// <summary>
        /// Add item into repository
        /// </summary>
        /// <param name="item">Item to add to repository</param>
        void Add(TEntity item);

        /// <summary>
        /// Delete item 
        /// </summary>
        /// <param name="item">Item to delete</param>
        void Remove(TEntity item);

        /// <summary>
        /// Set item as modified
        /// </summary>
        /// <param name="item">Item to modify</param>
        void Modify(TEntity item);

        /// <summary>
        ///Track entity into this repository, really in UnitOfWork. 
        ///In EF this can be done with Attach and with Update in NH
        /// </summary>
        /// <param name="item">Item to attach</param>
        void TrackItem(TEntity item);

        /// <summary>
        /// Sets modified entity into the repository. 
        /// When calling Commit() method in UnitOfWork 
        /// these changes will be saved into the storage
        /// </summary>
        /// <param name="persisted">The persisted item</param>
        /// <param name="current">The current item</param>
        void Merge(TEntity persisted, TEntity current);

        /// <summary>
        /// Delete filtered elements of type TEntity in repository
        /// </summary>
        /// <param name="filter"></param>
        /// <returns></returns>
        int DeleteMany(Expression<Func<TEntity, bool>> filter);

        /// <summary>
        /// Delete specified elements of type TEntity in repository
        /// </summary>
        /// <param name="specification"></param>
        /// <returns></returns>
        int DeleteMany(ISpecification<TEntity> specification);

        /// <summary>
        /// Update filtered elements of type TEntity in repository
        /// </summary>
        /// <param name="filter"></param>
        /// <param name="updateFactory"></param>
        /// <returns></returns>
        int UpdateMany(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateFactory);

        /// <summary>
        /// Update specified elements of type TEntity in repository
        /// </summary>
        /// <param name="specification"></param>
        /// <param name="updateFactory"></param>
        /// <returns></returns>
        int UpdateMany(ISpecification<TEntity> specification, Expression<Func<TEntity, TEntity>> updateFactory);
    }

    public interface ICommandRepository<TUnitOfWork, TEntity, TKey> : ICommandRepository<TEntity, TKey>, IRepository<TUnitOfWork>
        where TUnitOfWork : IUnitOfWork
        where TEntity : class, IEntity, new()
    {
    }
}
