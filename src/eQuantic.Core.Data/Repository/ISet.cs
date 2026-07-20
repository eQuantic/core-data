using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace eQuantic.Core.Data.Repository;

/// <summary>
/// The database set interface.
/// </summary>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <seealso cref="System.Linq.IQueryable{TEntity}" />
public interface ISet<TEntity> : IQueryable<TEntity> where TEntity : class, IEntity
{
    /// <summary>
    /// Deletes many entities matching the supplied predicate.
    /// </summary>
    /// <param name="filter">The predicate.</param>
    /// <returns>The number of deleted entities.</returns>
    long DeleteMany(Expression<Func<TEntity, bool>> filter);

    /// <summary>
    /// Deletes many entities matching the supplied predicate asynchronously.
    /// </summary>
    /// <param name="filter">The predicate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of deleted entities.</returns>
    Task<long> DeleteManyAsync(Expression<Func<TEntity, bool>> filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes the current query.
    /// </summary>
    /// <returns>The materialized entities.</returns>
    IEnumerable<TEntity> Execute();

    /// <summary>
    /// Finds the entity with the specified key.
    /// </summary>
    /// <typeparam name="TKey">The type of the key.</typeparam>
    /// <param name="key">The key.</param>
    /// <returns>The entity, or <c>null</c> when not found.</returns>
    TEntity? Find<TKey>(TKey key);

    /// <summary>
    /// Finds the entity with the specified key asynchronously.
    /// </summary>
    /// <typeparam name="TKey">The type of the key.</typeparam>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The entity, or <c>null</c> when not found.</returns>
    Task<TEntity?> FindAsync<TKey>(TKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts the specified item.
    /// </summary>
    /// <param name="item">The item.</param>
    void Insert(TEntity item);

    /// <summary>
    /// Inserts the specified item asynchronously.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task InsertAsync(TEntity item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates many entities matching the supplied predicate.
    /// </summary>
    /// <param name="filter">The predicate.</param>
    /// <param name="updateExpression">The update expression.</param>
    /// <returns>The number of updated entities.</returns>
    long UpdateMany(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateExpression);

    /// <summary>
    /// Updates many entities matching the supplied predicate asynchronously.
    /// </summary>
    /// <param name="filter">The predicate.</param>
    /// <param name="updateExpression">The update expression.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of updated entities.</returns>
    Task<long> UpdateManyAsync(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateExpression, CancellationToken cancellationToken = default);
}
