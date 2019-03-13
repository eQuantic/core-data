using eQuantic.Core.Data.Repository.Command;
using eQuantic.Core.Data.Repository.Query;

namespace eQuantic.Core.Data.Repository
{
    /// <summary>
    /// </summary>
    public interface IAsyncRepository : IRepository
    {
    }

    /// <summary>
    /// </summary>
    /// <typeparam name="TUnitOfWork"></typeparam>
    public interface IAsyncRepository<TUnitOfWork> : IAsyncRepository, IRepository<TUnitOfWork>
        where TUnitOfWork : IUnitOfWork
    {
    }

    /// <summary>
    /// </summary>
    /// <typeparam name="TEntity"></typeparam>
    /// <typeparam name="TKey"></typeparam>
    public interface IAsyncRepository<TEntity, TKey> : IAsyncQueryRepository<TEntity, TKey>, IAsyncCommandRepository<TEntity, TKey>
        where TEntity : class, IEntity, new()
    {
    }

    /// <summary>
    /// </summary>
    /// <typeparam name="TUnitOfWork"></typeparam>
    /// <typeparam name="TEntity"></typeparam>
    /// <typeparam name="TKey"></typeparam>
    public interface IAsyncRepository<TUnitOfWork, TEntity, TKey> : IAsyncQueryRepository<TUnitOfWork, TEntity, TKey>, IAsyncCommandRepository<TUnitOfWork, TEntity, TKey>
        where TUnitOfWork : IUnitOfWork
        where TEntity : class, IEntity, new()
    { }
}