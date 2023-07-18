using eQuantic.Core.Data.Repository.Config;

namespace eQuantic.Core.Data.Repository.Read;

/// <summary>
/// The read repository with joins
/// </summary>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <seealso cref="IReadRepository{TConfig,TEntity,TKey}" />
public interface IQueryableReadRepository<TEntity, in TKey> : IReadRepository<QueryableConfiguration<TEntity>, TEntity, TKey>
    where TEntity : class, IEntity, new()
{

}

/// <summary>
/// The read repository with joins
/// </summary>
/// <typeparam name="TUnitOfWork">The type of the unit of work.</typeparam>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <seealso cref="IReadRepository{TConfig,TEntity,TKey}" />
public interface IQueryableReadRepository<out TUnitOfWork, TEntity, in TKey> : IQueryableReadRepository<TEntity, TKey>, IRepository<TUnitOfWork>
    where TUnitOfWork : IUnitOfWork
    where TEntity : class, IEntity, new()
{
}
