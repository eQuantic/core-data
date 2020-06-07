using System;
using System.Linq.Expressions;
using System.Threading.Tasks;
using eQuantic.Core.Linq.Specification;

namespace eQuantic.Core.Data.Repository.Write
{
    /// <summary>
    /// The asynchronous write repository
    /// </summary>
    /// <typeparam name="TUnitOfWork">The type of the unit of work.</typeparam>
    /// <typeparam name="TEntity">The type of the entity.</typeparam>
    /// <typeparam name="TKey">The type of the key.</typeparam>
    /// <seealso cref="eQuantic.Core.Data.Repository.Write.IAsyncWriteRepository{TEntity, TKey}" />
    /// <seealso cref="eQuantic.Core.Data.Repository.IAsyncRepository{TUnitOfWork}" />
    public interface IAsyncWriteRepository<TUnitOfWork, TEntity, TKey> : IAsyncWriteRepository<TEntity, TKey>, IAsyncRepository<TUnitOfWork>
        where TUnitOfWork : IUnitOfWork
        where TEntity : class, IEntity, new()
    { }

    /// <summary>
    /// The asynchronous write repository
    /// </summary>
    /// <typeparam name="TEntity">The type of the entity.</typeparam>
    /// <typeparam name="TKey">The type of the key.</typeparam>
    /// <seealso cref="eQuantic.Core.Data.Repository.Write.IAsyncWriteRepository{TEntity, TKey}" />
    /// <seealso cref="eQuantic.Core.Data.Repository.IAsyncRepository{TUnitOfWork}" />
    public interface IAsyncWriteRepository<TEntity, TKey> : IAsyncRepository
        where TEntity : class, IEntity, new()
    {
        /// <summary>
        /// Add item into repository
        /// </summary>
        /// <param name="item">Item to add to repository</param>
        Task AddAsync(TEntity item);

        /// <summary>
        /// Delete filtered elements of type TEntity in repository
        /// </summary>
        /// <param name="filter"></param>
        /// <returns></returns>
        Task<long> DeleteManyAsync(Expression<Func<TEntity, bool>> filter);

        /// <summary>
        /// Delete specified elements of type TEntity in repository
        /// </summary>
        /// <param name="specification"></param>
        /// <returns></returns>
        Task<long> DeleteManyAsync(ISpecification<TEntity> specification);

        /// <summary>
        /// Sets modified entity into the repository. When calling Commit() method in UnitOfWork
        /// these changes will be saved into the storage
        /// </summary>
        /// <param name="persisted">The persisted item</param>
        /// <param name="current">The current item</param>
        Task MergeAsync(TEntity persisted, TEntity current);

        /// <summary>
        /// Set item as modified
        /// </summary>
        /// <param name="item">Item to modify</param>
        Task ModifyAsync(TEntity item);

        /// <summary>
        /// Delete item
        /// </summary>
        /// <param name="item">Item to delete</param>
        Task RemoveAsync(TEntity item);

        /// <summary>
        /// Update filtered elements of type TEntity in repository
        /// </summary>
        /// <param name="filter"></param>
        /// <param name="updateFactory"></param>
        /// <returns></returns>
        Task<long> UpdateManyAsync(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateFactory);

        /// <summary>
        /// Update specified elements of type TEntity in repository
        /// </summary>
        /// <param name="specification"></param>
        /// <param name="updateFactory"></param>
        /// <returns></returns>
        Task<long> UpdateManyAsync(ISpecification<TEntity> specification, Expression<Func<TEntity, TEntity>> updateFactory);
    }
}