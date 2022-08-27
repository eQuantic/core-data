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
}