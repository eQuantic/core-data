using eQuantic.Core.Data.Repository.Config;
using eQuantic.Core.Data.Repository.Read;
using eQuantic.Core.Data.Repository.Write;

namespace eQuantic.Core.Data.Repository;

/// <summary>
/// The asynchronous repository
/// </summary>
public interface IAsyncRepository : IRepository
{
}

/// <summary>
/// The asynchronous repository
/// </summary>
/// <typeparam name="TUnitOfWork"></typeparam>
public interface IAsyncRepository<out TUnitOfWork> : IAsyncRepository, IRepository<TUnitOfWork>
    where TUnitOfWork : IUnitOfWork
{
}

/// <summary>
/// The asynchronous repository
/// </summary>
/// <typeparam name="TEntity"></typeparam>
/// <typeparam name="TKey"></typeparam>
public interface IAsyncRepository<TEntity, TKey> : IAsyncReadRepository<Configuration<TEntity>, TEntity, TKey>, IAsyncWriteRepository<TEntity>
    where TEntity : class, IEntity, new()
{
}

/// <summary>
/// The asynchronous repository
/// </summary>
/// <typeparam name="TUnitOfWork"></typeparam>
/// <typeparam name="TEntity"></typeparam>
/// <typeparam name="TKey"></typeparam>
public interface IAsyncRepository<out TUnitOfWork, TEntity, TKey> : IAsyncReadRepository<TUnitOfWork, Configuration<TEntity>, TEntity, TKey>, IAsyncWriteRepository<TUnitOfWork, TEntity>
    where TUnitOfWork : IUnitOfWork
    where TEntity : class, IEntity, new()
{ }

/// <summary>
/// The asynchronous repository
/// </summary>
/// <typeparam name="TUnitOfWork"></typeparam>
/// <typeparam name="TConfig"></typeparam>
/// <typeparam name="TEntity"></typeparam>
/// <typeparam name="TKey"></typeparam>
public interface IAsyncRepository<out TUnitOfWork, out TConfig, TEntity, TKey> : IAsyncReadRepository<TUnitOfWork, TConfig, TEntity, TKey>, IAsyncWriteRepository<TUnitOfWork, TEntity>
    where TUnitOfWork : IUnitOfWork
    where TEntity : class, IEntity, new()
    where TConfig : Configuration<TEntity>
{ }
