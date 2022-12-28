namespace eQuantic.Core.Data.Repository;

/// <summary>
/// The Queryable Unit of Work interface
/// </summary>
/// <seealso cref="eQuantic.Core.Data.Repository.IUnitOfWork" />
public interface IQueryableUnitOfWork : IUnitOfWork
{
    /// <summary>
    /// Returns a IQueryable instance for access to entities of the given type in the context
    /// </summary>
    /// <typeparam name="TEntity"></typeparam>
    /// <returns></returns>
    ISet<TEntity> CreateSet<TEntity>() where TEntity : class, IEntity, new();
    
    /// <summary>
    /// Gets the queryable entity repository instance
    /// </summary>
    /// <typeparam name="TUnitOfWork">The unit of work</typeparam>
    /// <typeparam name="TEntity">The entity</typeparam>
    /// <typeparam name="TKey">The key of entity</typeparam>
    /// <returns></returns>
    IQueryableRepository<TUnitOfWork, TEntity, TKey> GetQueryableRepository<TUnitOfWork, TEntity, TKey>() where TEntity : class, IEntity, new() where TUnitOfWork : IQueryableUnitOfWork;

    /// <summary>
    /// Gets the queryable entity repository instance
    /// </summary>
    /// <typeparam name="TUnitOfWork">The unit of work</typeparam>
    /// <typeparam name="TEntity">The entity</typeparam>
    /// <typeparam name="TKey">The key of entity</typeparam>
    /// <param name="name"></param>
    /// <returns></returns>
    IQueryableRepository<TUnitOfWork, TEntity, TKey> GetQueryableRepository<TUnitOfWork, TEntity, TKey>(string name) where TEntity : class, IEntity, new() where TUnitOfWork : IQueryableUnitOfWork;
    
    /// <summary>
    /// Gets the asynchronous queryable entity repository instance
    /// </summary>
    /// <typeparam name="TUnitOfWork">The unit of work</typeparam>
    /// <typeparam name="TEntity">The entity</typeparam>
    /// <typeparam name="TKey">The key of entity</typeparam>
    /// <returns></returns>
    IAsyncQueryableRepository<TUnitOfWork, TEntity, TKey> GetAsyncQueryableRepository<TUnitOfWork, TEntity, TKey>() where TEntity : class, IEntity, new() where TUnitOfWork : IQueryableUnitOfWork;

    /// <summary>
    /// Gets the asynchronous queryable entity repository instance
    /// </summary>
    /// <typeparam name="TUnitOfWork">The unit of work</typeparam>
    /// <typeparam name="TEntity">The entity</typeparam>
    /// <typeparam name="TKey">The key of entity</typeparam>
    /// <param name="name"></param>
    /// <returns></returns>
    IAsyncQueryableRepository<TUnitOfWork, TEntity, TKey> GetAsyncQueryableRepository<TUnitOfWork, TEntity, TKey>(string name) where TEntity : class, IEntity, new() where TUnitOfWork : IQueryableUnitOfWork;
}