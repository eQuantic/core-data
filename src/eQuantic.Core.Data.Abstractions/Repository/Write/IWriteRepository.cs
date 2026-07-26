using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using eQuantic.Linq.Specification;

namespace eQuantic.Core.Data.Repository.Write;

/// <summary>
/// The synchronous write repository.
/// </summary>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <seealso cref="IRepository" />
public interface IWriteRepository<TEntity> : IRepository
    where TEntity : class, IEntity
{
    /// <summary>
    /// Adds an item into the repository.
    /// </summary>
    /// <param name="item">The item to add.</param>
    void Add(TEntity item);

    /// <summary>
    /// Adds a range of items into the repository.
    /// </summary>
    /// <param name="items">The items to add.</param>
    void AddRange(IEnumerable<TEntity> items);

    /// <summary>
    /// Deletes the elements matching the supplied predicate.
    /// </summary>
    /// <param name="filter">The predicate to apply.</param>
    /// <returns>The number of deleted elements.</returns>
    long DeleteMany(Expression<Func<TEntity, bool>> filter);

    /// <summary>
    /// Deletes the elements matching the supplied specification.
    /// </summary>
    /// <param name="specification">The specification to apply.</param>
    /// <returns>The number of deleted elements.</returns>
    long DeleteMany(ISpecification<TEntity> specification);

    /// <summary>
    /// Sets a modified entity into the repository. Changes are persisted when the
    /// unit of work is committed.
    /// </summary>
    /// <param name="persisted">The persisted item.</param>
    /// <param name="current">The current item.</param>
    void Merge(TEntity persisted, TEntity current);

    /// <summary>
    /// Marks an item as modified.
    /// </summary>
    /// <param name="item">The item to modify.</param>
    void Modify(TEntity item);

    /// <summary>
    /// Removes an item.
    /// </summary>
    /// <param name="item">The item to remove.</param>
    void Remove(TEntity item);

    /// <summary>
    /// Tracks an item into this repository (through the unit of work).
    /// </summary>
    /// <param name="item">The item to track.</param>
    void TrackItem(TEntity item);

    /// <summary>
    /// Updates the elements matching the supplied predicate.
    /// </summary>
    /// <param name="filter">The predicate to apply.</param>
    /// <param name="updateFactory">The update expression.</param>
    /// <returns>The number of updated elements.</returns>
    long UpdateMany(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateFactory);

    /// <summary>
    /// Updates the elements matching the supplied specification.
    /// </summary>
    /// <param name="specification">The specification to apply.</param>
    /// <param name="updateFactory">The update expression.</param>
    /// <returns>The number of updated elements.</returns>
    long UpdateMany(ISpecification<TEntity> specification, Expression<Func<TEntity, TEntity>> updateFactory);
}
