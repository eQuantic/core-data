using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace eQuantic.Core.Data.Repository
{
    /// <summary>
    /// The database Set interface
    /// </summary>
    /// <typeparam name="TEntity">The type of the entity.</typeparam>
    /// <seealso cref="System.Linq.IQueryable{TEntity}" />
    public interface ISet<TEntity> : IQueryable<TEntity> where TEntity : class, IEntity, new()
    {
        /// <summary>
        /// Deletes many entities.
        /// </summary>
        /// <param name="filter">The filter.</param>
        /// <returns></returns>
        long DeleteMany(Expression<Func<TEntity, bool>> filter);

        /// <summary>
        /// Deletes many entities asynchronous.
        /// </summary>
        /// <param name="filter">The filter.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        Task<long> DeleteManyAsync(Expression<Func<TEntity, bool>> filter, CancellationToken cancellationToken = default);

        /// <summary>
        /// Executes the current query.
        /// </summary>
        /// <returns></returns>
        IEnumerable<TEntity> Execute();

        /// <summary>
        /// Finds the specified key.
        /// </summary>
        /// <typeparam name="TKey">The type of the key.</typeparam>
        /// <param name="key">The key.</param>
        /// <returns></returns>
        TEntity Find<TKey>(TKey key);

        /// <summary>
        /// Finds the asynchronous.
        /// </summary>
        /// <typeparam name="TKey">The type of the key.</typeparam>
        /// <param name="key">The key.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        Task<TEntity> FindAsync<TKey>(TKey key, CancellationToken cancellationToken = default);

        /// <summary>
        /// Inserts the specified item.
        /// </summary>
        /// <param name="item">The item.</param>
        void Insert(TEntity item);

        /// <summary>
        /// Inserts the asynchronous.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        Task InsertAsync(TEntity item, CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates many entities by criteria.
        /// </summary>
        /// <param name="filter">The filter.</param>
        /// <param name="updateExpression">The update expression.</param>
        /// <returns></returns>
        long UpdateMany(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateExpression);

        /// <summary>
        /// Updates many entities by criteria asynchronous.
        /// </summary>
        /// <param name="filter">The filter.</param>
        /// <param name="updateExpression">The update expression.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        Task<long> UpdateManyAsync(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateExpression, CancellationToken cancellationToken = default);
    }
}