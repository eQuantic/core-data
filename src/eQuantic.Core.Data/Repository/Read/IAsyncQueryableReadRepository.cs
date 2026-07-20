namespace eQuantic.Core.Data.Repository.Read;

/// <summary>
/// An asynchronous read repository backed by an <see cref="System.Linq.IQueryable{T}"/> data source.
/// </summary>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <seealso cref="IAsyncReadRepository{TEntity, TKey}" />
public interface IAsyncQueryableReadRepository<TEntity, TKey> : IAsyncReadRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
{
}
