namespace eQuantic.Core.Data.Repository;

/// <summary>
/// A unit of work whose repositories are backed by an <see cref="System.Linq.IQueryable{T}"/> data source.
/// </summary>
public interface IQueryableUnitOfWork : IUnitOfWork
{
    /// <summary>
    /// Returns an <see cref="ISet{TEntity}"/> for accessing entities of the given type in the context.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <returns>The entity set.</returns>
    ISet<TEntity> CreateSet<TEntity>() where TEntity : class, IEntity;

    /// <summary>
    /// Gets the queryable entity repository instance.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <typeparam name="TKey">The type of the entity key.</typeparam>
    /// <returns>The queryable repository.</returns>
    IQueryableRepository<TEntity, TKey> GetQueryableRepository<TEntity, TKey>()
        where TEntity : class, IEntity<TKey>;

    /// <summary>
    /// Gets the asynchronous queryable entity repository instance.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <typeparam name="TKey">The type of the entity key.</typeparam>
    /// <returns>The asynchronous queryable repository.</returns>
    IAsyncQueryableRepository<TEntity, TKey> GetAsyncQueryableRepository<TEntity, TKey>()
        where TEntity : class, IEntity<TKey>;
}
