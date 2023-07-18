using eQuantic.Core.Data.Repository.Config;

namespace eQuantic.Core.Data.Repository.Read;

/// <summary>
/// The asynchronous read repository with specification pattern
/// </summary>
/// <typeparam name="TUnitOfWork">The type of the unit of work.</typeparam>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <seealso cref="IAsyncReadRepository{TConfig,TEntity,TKey}" />
public interface IAsyncQueryableReadRepository<out TUnitOfWork, TEntity, in TKey> : IAsyncQueryableReadRepository<TEntity, TKey>, IAsyncRepository<TUnitOfWork>
    where TUnitOfWork : IUnitOfWork
    where TEntity : class, IEntity, new()
{
}

/// <summary>
/// The asynchronous read repository with specification pattern
/// </summary>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <seealso cref="IAsyncReadRepository{TConfig,TEntity,TKey}" />
public interface IAsyncQueryableReadRepository<TEntity, in TKey> : IAsyncReadRepository<QueryableConfiguration<TEntity>, TEntity, TKey>
    where TEntity : class, IEntity, new()
{

}
