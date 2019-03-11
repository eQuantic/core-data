using eQuantic.Core.Linq.Specification;
using System;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace eQuantic.Core.Data.Repository.Command
{
    public interface IAsyncCommandRepository<TEntity, TKey> : IAsyncRepository
        where TEntity : class, IEntity, new()
    {
        /// <summary>
        /// Delete filtered elements of type TEntity in repository
        /// </summary>
        /// <param name="filter"></param>
        /// <returns></returns>
        Task<int> DeleteManyAsync(Expression<Func<TEntity, bool>> filter);

        /// <summary>
        /// Delete specified elements of type TEntity in repository
        /// </summary>
        /// <param name="specification"></param>
        /// <returns></returns>
        Task<int> DeleteManyAsync(ISpecification<TEntity> specification);

        /// <summary>
        /// Update filtered elements of type TEntity in repository
        /// </summary>
        /// <param name="filter"></param>
        /// <param name="updateFactory"></param>
        /// <returns></returns>
        Task<int> UpdateManyAsync(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateFactory);

        /// <summary>
        /// Update specified elements of type TEntity in repository
        /// </summary>
        /// <param name="specification"></param>
        /// <param name="updateFactory"></param>
        /// <returns></returns>
        Task<int> UpdateManyAsync(ISpecification<TEntity> specification, Expression<Func<TEntity, TEntity>> updateFactory);
    }

    public interface IAsyncCommandRepository<TUnitOfWork, TEntity, TKey> : IAsyncCommandRepository<TEntity, TKey>, IAsyncRepository<TUnitOfWork>
        where TUnitOfWork : IUnitOfWork
        where TEntity : class, IEntity, new()
    {
    }
}
