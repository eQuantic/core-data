using eQuantic.Core.Data.Repository.Read;
using eQuantic.Core.Data.Repository.Write;

namespace eQuantic.Core.Data.Repository;

/// <summary>
/// The asynchronous repository with specification pattern
/// </summary>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <seealso cref="eQuantic.Core.Data.Repository.Read.IAsyncRelationalReadRepository{TEntity, TKey}" />
/// <seealso cref="eQuantic.Core.Data.Repository.Write.IAsyncWriteRepository{TEntity, TKey}" />
public interface IAsyncRelationalRepository<TEntity, TKey> : IAsyncRelationalReadRepository<TEntity, TKey>, IAsyncWriteRepository<TEntity, TKey>
    where TEntity : class, IEntity, new()
{
}

/// <summary>
/// The asynchronous repository with specification pattern
/// </summary>
/// <typeparam name="TUnitOfWork">The type of the unit of work.</typeparam>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <seealso cref="eQuantic.Core.Data.Repository.Read.IAsyncRelationalReadRepository{TEntity, TKey}" />
/// <seealso cref="eQuantic.Core.Data.Repository.Write.IAsyncWriteRepository{TEntity, TKey}" />
public interface IAsyncRelationalRepository<TUnitOfWork, TEntity, TKey> : IAsyncRelationalReadRepository<TUnitOfWork, TEntity, TKey>, IAsyncWriteRepository<TUnitOfWork, TEntity, TKey>
    where TUnitOfWork : IUnitOfWork
    where TEntity : class, IEntity, new()
{
}