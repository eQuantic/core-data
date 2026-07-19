using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using eQuantic.Linq.Specification;

namespace eQuantic.Core.Data.Repository.Write;

/// <summary>
/// The asynchronous write repository.
/// </summary>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <seealso cref="IAsyncRepository" />
public interface IAsyncWriteRepository<TEntity> : IAsyncRepository
    where TEntity : class, IEntity
{
    /// <summary>
    /// Adds an item into the repository.
    /// </summary>
    /// <param name="item">The item to add.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task AddAsync(TEntity item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a range of items into the repository.
    /// </summary>
    /// <param name="items">The items to add.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task AddRangeAsync(IEnumerable<TEntity> items, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the elements matching the supplied predicate.
    /// </summary>
    /// <param name="filter">The predicate to apply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of deleted elements.</returns>
    Task<long> DeleteManyAsync(Expression<Func<TEntity, bool>> filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the elements matching the supplied specification.
    /// </summary>
    /// <param name="specification">The specification to apply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of deleted elements.</returns>
    Task<long> DeleteManyAsync(ISpecification<TEntity> specification, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a modified entity into the repository. Changes are persisted when the
    /// unit of work is committed.
    /// </summary>
    /// <param name="persisted">The persisted item.</param>
    /// <param name="current">The current item.</param>
    Task MergeAsync(TEntity persisted, TEntity current);

    /// <summary>
    /// Marks an item as modified.
    /// </summary>
    /// <param name="item">The item to modify.</param>
    Task ModifyAsync(TEntity item);

    /// <summary>
    /// Removes an item.
    /// </summary>
    /// <param name="item">The item to remove.</param>
    Task RemoveAsync(TEntity item);

    /// <summary>
    /// Updates the elements matching the supplied predicate.
    /// </summary>
    /// <param name="filter">The predicate to apply.</param>
    /// <param name="updateFactory">The update expression.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of updated elements.</returns>
    Task<long> UpdateManyAsync(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateFactory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the elements matching the supplied specification.
    /// </summary>
    /// <param name="specification">The specification to apply.</param>
    /// <param name="updateFactory">The update expression.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of updated elements.</returns>
    Task<long> UpdateManyAsync(ISpecification<TEntity> specification, Expression<Func<TEntity, TEntity>> updateFactory, CancellationToken cancellationToken = default);
}
