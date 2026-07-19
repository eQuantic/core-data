using eQuantic.Core.Data.Repository.Read;
using eQuantic.Core.Data.Repository.Write;

namespace eQuantic.Core.Data.Repository;

/// <summary>
/// The asynchronous repository marker interface.
/// </summary>
/// <seealso cref="IRepository" />
public interface IAsyncRepository : IRepository
{
}

/// <summary>
/// The asynchronous repository, composed of the asynchronous read and write repositories.
/// </summary>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
public interface IAsyncRepository<TEntity, TKey> : IAsyncReadRepository<TEntity, TKey>, IAsyncWriteRepository<TEntity>
    where TEntity : class, IEntity<TKey>
{
}
