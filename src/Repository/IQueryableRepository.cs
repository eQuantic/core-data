using eQuantic.Core.Data.Repository.Read;
using eQuantic.Core.Data.Repository.Write;

namespace eQuantic.Core.Data.Repository;

/// <summary>
/// A synchronous repository backed by an <see cref="System.Linq.IQueryable{T}"/> data source.
/// </summary>
/// <typeparam name="TEntity">The type of the entity for this repository.</typeparam>
/// <typeparam name="TKey">The type of the primary key for this entity.</typeparam>
public interface IQueryableRepository<TEntity, TKey> : IQueryableReadRepository<TEntity, TKey>, IWriteRepository<TEntity>
    where TEntity : class, IEntity<TKey>
{
}
