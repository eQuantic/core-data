using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using eQuantic.Core.Data.Repository.Config;
using eQuantic.Linq.Specification;

namespace eQuantic.Core.Data.Repository.Read;

/// <summary>
/// The asynchronous read repository
/// </summary>
/// <typeparam name="TConfig">The source configuration.</typeparam>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <seealso cref="IAsyncRepository" />
public interface IAsyncReadRepository<out TConfig, TEntity, in TKey> : IAsyncRepository
    where TEntity : class, IEntity, new()
    where TConfig : Configuration<TEntity>
{
    /// <summary>
    /// Get all elements of type TEntity that matching a
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> AllMatchingAsync(
        ISpecification<TEntity> specification,
        Action<TConfig> configuration = default);

    /// <summary>
    /// Get all elements of type TEntity that matching a
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="configuration"></param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> AllMatchingAsync(
        ISpecification<TEntity> specification,
        Action<TConfig> configuration, 
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Get all elements of type TEntity that matching a
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> AllMatchingAsync(
        ISpecification<TEntity> specification, 
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Count elements of type TEntity in repository
    /// </summary>
    /// <returns></returns>
    Task<long> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Count specified elements of type TEntity in repository
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<long> CountAsync(
        ISpecification<TEntity> specification, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Count filtered elements of type TEntity in repository
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<long> CountAsync(
        Expression<Func<TEntity, bool>> filter, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verify all specified elements of type TEntity in repository
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    Task<bool> AllAsync(
        ISpecification<TEntity> specification, 
        Action<TConfig> configuration = default);

    /// <summary>
    /// Verify all specified elements of type TEntity in repository
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="configuration"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<bool> AllAsync(
        ISpecification<TEntity> specification, 
        Action<TConfig> configuration,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Verify all specified elements of type TEntity in repository
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<bool> AllAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Verify all filtered elements of type TEntity in repository
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    Task<bool> AllAsync(
        Expression<Func<TEntity, bool>> filter, 
        Action<TConfig> configuration = default);

    /// <summary>
    /// Verify all filtered elements of type TEntity in repository
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="configuration"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<bool> AllAsync(
        Expression<Func<TEntity, bool>> filter, 
        Action<TConfig> configuration,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Verify all filtered elements of type TEntity in repository
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<bool> AllAsync(
        Expression<Func<TEntity, bool>> filter,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Verify any elements of type TEntity in repository
    /// </summary>
    /// <param name="configuration"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<bool> AnyAsync(
        Action<TConfig> configuration = default, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verify any specified elements of type TEntity in repository
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    Task<bool> AnyAsync(
        ISpecification<TEntity> specification, 
        Action<TConfig> configuration = default);

    /// <summary>
    /// Verify any specified elements of type TEntity in repository
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="configuration"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<bool> AnyAsync(
        ISpecification<TEntity> specification, 
        Action<TConfig> configuration,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Verify any specified elements of type TEntity in repository
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<bool> AnyAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Verify any filtered elements of type TEntity in repository
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    Task<bool> AnyAsync(
        Expression<Func<TEntity, bool>> filter, 
        Action<TConfig> configuration = default);

    /// <summary>
    /// Verify any filtered elements of type TEntity in repository
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="configuration"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<bool> AnyAsync(
        Expression<Func<TEntity, bool>> filter, 
        Action<TConfig> configuration,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Verify any filtered elements of type TEntity in repository
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<bool> AnyAsync(
        Expression<Func<TEntity, bool>> filter,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Get all elements of type TEntity in repository
    /// </summary>
    /// <param name="configuration"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetAllAsync(
        Action<TConfig> configuration = default);

    /// <summary>
    /// Get all elements of type TEntity in repository
    /// </summary>
    /// <param name="configuration"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetAllAsync(
        Action<TConfig> configuration,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Get all elements of type TEntity in repository
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetAllAsync(
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Get element by entity key
    /// </summary>
    /// <param name="id"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    Task<TEntity> GetAsync(
        TKey id, 
        Action<TConfig> configuration = default);

    /// <summary>
    /// Get element by entity key
    /// </summary>
    /// <param name="id"></param>
    /// <param name="configuration"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<TEntity> GetAsync(
        TKey id, 
        Action<TConfig> configuration,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Get element by entity key
    /// </summary>
    /// <param name="id"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<TEntity> GetAsync(
        TKey id,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Get mapped elements from TEntity type by criteria
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="map"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    Task<IEnumerable<TResult>> GetMappedAsync<TResult>(
        Expression<Func<TEntity, bool>> filter,
        Expression<Func<TEntity, TResult>> map, 
        Action<TConfig> configuration = default);

    /// <summary>
    /// Get mapped elements from TEntity type by criteria
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="map"></param>
    /// <param name="configuration"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<TResult>> GetMappedAsync<TResult>(
        Expression<Func<TEntity, bool>> filter,
        Expression<Func<TEntity, TResult>> map, 
        Action<TConfig> configuration,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Get mapped elements from TEntity type by criteria
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="map"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<TResult>> GetMappedAsync<TResult>(
        Expression<Func<TEntity, bool>> filter,
        Expression<Func<TEntity, TResult>> map,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Get mapped elements from TEntity type by specification
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="map"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    Task<IEnumerable<TResult>> GetMappedAsync<TResult>(
        ISpecification<TEntity> specification,
        Expression<Func<TEntity, TResult>> map, 
        Action<TConfig> configuration = default);

    /// <summary>
    /// Get mapped elements from TEntity type by specification
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="map"></param>
    /// <param name="configuration"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<TResult>> GetMappedAsync<TResult>(
        ISpecification<TEntity> specification,
        Expression<Func<TEntity, TResult>> map, 
        Action<TConfig> configuration,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Get mapped elements from TEntity type by specification
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="map"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<TResult>> GetMappedAsync<TResult>(
        ISpecification<TEntity> specification,
        Expression<Func<TEntity, TResult>> map,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetFilteredAsync(
        Expression<Func<TEntity, bool>> filter,
        Action<TConfig> configuration = default);

    /// <summary>
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="configuration"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetFilteredAsync(
        Expression<Func<TEntity, bool>> filter,
        Action<TConfig> configuration, 
        CancellationToken cancellationToken);
    
    /// <summary>
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetFilteredAsync(
        Expression<Func<TEntity, bool>> filter,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Get first ordered element by criteria.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    Task<TEntity> GetFirstAsync(
        Expression<Func<TEntity, bool>> filter, 
        Action<TConfig> configuration = default);

    /// <summary>
    /// Get first ordered element by criteria.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="configuration"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<TEntity> GetFirstAsync(
        Expression<Func<TEntity, bool>> filter, 
        Action<TConfig> configuration,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Get first ordered element by criteria.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<TEntity> GetFirstAsync(
        Expression<Func<TEntity, bool>> filter,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Get first ordered element by specification.
    /// </summary>
    /// <param name="specification">The specification.</param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    Task<TEntity> GetFirstAsync(
        ISpecification<TEntity> specification, 
        Action<TConfig> configuration = default);

    /// <summary>
    /// Get first ordered element by specification.
    /// </summary>
    /// <param name="specification">The specification.</param>
    /// <param name="configuration"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<TEntity> GetFirstAsync(
        ISpecification<TEntity> specification, 
        Action<TConfig> configuration,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Get first ordered element by specification.
    /// </summary>
    /// <param name="specification">The specification.</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<TEntity> GetFirstAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Get first ordered element by criteria.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="map"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    Task<TResult> GetFirstMappedAsync<TResult>(
        Expression<Func<TEntity, bool>> filter,
        Expression<Func<TEntity, TResult>> map, 
        Action<TConfig> configuration = default);

    /// <summary>
    /// Get first ordered element by criteria.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="map"></param>
    /// <param name="configuration"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<TResult> GetFirstMappedAsync<TResult>(
        Expression<Func<TEntity, bool>> filter,
        Expression<Func<TEntity, TResult>> map, 
        Action<TConfig> configuration,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Get first ordered element by criteria.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="map"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<TResult> GetFirstMappedAsync<TResult>(
        Expression<Func<TEntity, bool>> filter,
        Expression<Func<TEntity, TResult>> map,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Get first ordered element by specification.
    /// </summary>
    /// <param name="specification">The specification.</param>
    /// <param name="map"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    Task<TResult> GetFirstMappedAsync<TResult>(
        ISpecification<TEntity> specification,
        Expression<Func<TEntity, TResult>> map, 
        Action<TConfig> configuration = default);

    /// <summary>
    /// Get first ordered element by specification.
    /// </summary>
    /// <param name="specification">The specification.</param>
    /// <param name="map"></param>
    /// <param name="configuration"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<TResult> GetFirstMappedAsync<TResult>(
        ISpecification<TEntity> specification,
        Expression<Func<TEntity, TResult>> map, 
        Action<TConfig> configuration,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Get first ordered element by specification.
    /// </summary>
    /// <param name="specification">The specification.</param>
    /// <param name="map"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<TResult> GetFirstMappedAsync<TResult>(
        ISpecification<TEntity> specification,
        Expression<Func<TEntity, TResult>> map,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// </summary>
    /// <param name="limit"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(
        int limit, 
        Action<TConfig> configuration = default);

    /// <summary>
    /// </summary>
    /// <param name="limit"></param>
    /// <param name="configuration"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(
        int limit, 
        Action<TConfig> configuration,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// </summary>
    /// <param name="limit"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(
        int limit,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="limit"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(
        ISpecification<TEntity> specification, 
        int limit,
        Action<TConfig> configuration = default);

    /// <summary>
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="limit"></param>
    /// <param name="configuration"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(
        ISpecification<TEntity> specification, 
        int limit,
        Action<TConfig> configuration, 
        CancellationToken cancellationToken);
    
    /// <summary>
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="limit"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(
        ISpecification<TEntity> specification, 
        int limit,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="limit"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(
        Expression<Func<TEntity, bool>> filter, 
        int limit,
        Action<TConfig> configuration = default);

    /// <summary>
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="limit"></param>
    /// <param name="configuration"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(
        Expression<Func<TEntity, bool>> filter, 
        int limit,
        Action<TConfig> configuration, 
        CancellationToken cancellationToken);
    
    /// <summary>
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="limit"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(
        Expression<Func<TEntity, bool>> filter, 
        int limit,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// </summary>
    /// <param name="pageIndex"></param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(
        int pageIndex, 
        int pageSize, 
        Action<TConfig> configuration = default);

    /// <summary>
    /// </summary>
    /// <param name="pageIndex"></param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="configuration"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(
        int pageIndex, 
        int pageSize, 
        Action<TConfig> configuration,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// </summary>
    /// <param name="pageIndex"></param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(
        int pageIndex, 
        int pageSize,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="pageIndex"></param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(
        ISpecification<TEntity> specification, 
        int pageIndex, 
        int pageSize,
        Action<TConfig> configuration = default);

    /// <summary>
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="pageIndex"></param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="configuration"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(
        ISpecification<TEntity> specification, 
        int pageIndex, 
        int pageSize,
        Action<TConfig> configuration, 
        CancellationToken cancellationToken);
    
    /// <summary>
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="pageIndex"></param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(
        ISpecification<TEntity> specification, 
        int pageIndex, 
        int pageSize,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="pageIndex"></param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(
        Expression<Func<TEntity, bool>> filter, 
        int pageIndex, 
        int pageSize,
        Action<TConfig> configuration = default);

    /// <summary>
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="pageIndex"></param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="configuration"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(
        Expression<Func<TEntity, bool>> filter, 
        int pageIndex, 
        int pageSize,
        Action<TConfig> configuration, 
        CancellationToken cancellationToken);
    
    /// <summary>
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="pageIndex"></param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(
        Expression<Func<TEntity, bool>> filter, 
        int pageIndex, 
        int pageSize,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Get single ordered element by criteria.
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    Task<TEntity> GetSingleAsync(
        Expression<Func<TEntity, bool>> filter, 
        Action<TConfig> configuration = default);

    /// <summary>
    /// Get single ordered element by criteria.
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="configuration"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<TEntity> GetSingleAsync(
        Expression<Func<TEntity, bool>> filter, 
        Action<TConfig> configuration,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Get single ordered element by criteria.
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<TEntity> GetSingleAsync(
        Expression<Func<TEntity, bool>> filter,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Get single ordered element by specification.
    /// </summary>
    /// <param name="specification">The specification.</param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    Task<TEntity> GetSingleAsync(
        ISpecification<TEntity> specification, 
        Action<TConfig> configuration = default);
    
    /// <summary>
    /// Get single ordered element by specification.
    /// </summary>
    /// <param name="specification">The specification.</param>
    /// <param name="configuration"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<TEntity> GetSingleAsync(
        ISpecification<TEntity> specification, 
        Action<TConfig> configuration,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Get single ordered element by specification.
    /// </summary>
    /// <param name="specification">The specification.</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<TEntity> GetSingleAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken);
}

/// <summary>
/// The asynchronous read repository
/// </summary>
/// <typeparam name="TUnitOfWork">The type of the unit of work.</typeparam>
/// <typeparam name="TConfig">The source configuration.</typeparam>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <seealso cref="IAsyncRepository" />
public interface
    IAsyncReadRepository<out TUnitOfWork, out TConfig, TEntity, in TKey> : IAsyncReadRepository<TConfig, TEntity, TKey>,
        IAsyncRepository<TUnitOfWork>
    where TUnitOfWork : IUnitOfWork
    where TEntity : class, IEntity, new()
    where TConfig : Configuration<TEntity>
{
}
