namespace eQuantic.Core.Data.Repository.Read;

/// <summary>
/// A synchronous read repository backed by an <see cref="System.Linq.IQueryable{T}"/> data source.
/// </summary>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <seealso cref="IReadRepository{TEntity, TKey}" />
public interface IQueryableReadRepository<TEntity, TKey> : IReadRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
{
}
