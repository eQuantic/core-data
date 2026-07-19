using eQuantic.Core.Data.Repository.Read;
using eQuantic.Core.Data.Repository.Write;

namespace eQuantic.Core.Data.Repository;

/// <summary>
/// An asynchronous repository backed by an <see cref="System.Linq.IQueryable{T}"/> data source.
/// </summary>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
public interface IAsyncQueryableRepository<TEntity, TKey> : IAsyncQueryableReadRepository<TEntity, TKey>, IAsyncWriteRepository<TEntity>
    where TEntity : class, IEntity<TKey>
{
}
