using System;
using eQuantic.Core.Data.Repository.Read;
using eQuantic.Core.Data.Repository.Write;

namespace eQuantic.Core.Data.Repository;

/// <summary>
/// Repository marker interface.
/// </summary>
/// <seealso cref="System.IDisposable" />
public interface IRepository : IDisposable
{
}

/// <summary>
/// Base interface for the "Repository Pattern". For more information about this
/// pattern see http://martinfowler.com/eaaCatalog/repository.html.
/// </summary>
/// <typeparam name="TEntity">The type of the entity for this repository.</typeparam>
/// <typeparam name="TKey">The type of the primary key for this entity.</typeparam>
public interface IRepository<TEntity, TKey> : IReadRepository<TEntity, TKey>, IWriteRepository<TEntity>
    where TEntity : class, IEntity<TKey>
{
}
