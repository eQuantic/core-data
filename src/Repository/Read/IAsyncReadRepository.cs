using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading.Tasks;
using eQuantic.Linq.Sorter;
using eQuantic.Linq.Specification;

namespace eQuantic.Core.Data.Repository.Read;

/// <summary>
/// The asynchronous read repository
/// </summary>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <seealso cref="eQuantic.Core.Data.Repository.IAsyncRepository" />
public interface IAsyncReadRepository<TEntity, TKey> : IAsyncRepository
    where TEntity : class, IEntity, new()
{
    /// <summary>
    /// Get all elements of type TEntity that matching a
    /// </summary>
    /// <param name="specification"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> AllMatchingAsync(ISpecification<TEntity> specification);

    /// <summary>
    /// Count elements of type TEntity in repository
    /// </summary>
    /// <returns></returns>
    Task<long> CountAsync();

    /// <summary>
    /// Count specified elements of type TEntity in repository
    /// </summary>
    /// <param name="specification"></param>
    /// <returns></returns>
    Task<long> CountAsync(ISpecification<TEntity> specification);

    /// <summary>
    /// Count filtered elements of type TEntity in repository
    /// </summary>
    /// <param name="filter"></param>
    /// <returns></returns>
    Task<long> CountAsync(Expression<Func<TEntity, bool>> filter);

    /// <summary>
    /// Get all elements of type TEntity in repository
    /// </summary>
    /// <param name="sortingColumns"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetAllAsync(params ISorting[] sortingColumns);

    /// <summary>
    /// Get element by entity key
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    Task<TEntity> GetAsync(TKey id);

    /// <summary>
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="sortColumns"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetFilteredAsync(Expression<Func<TEntity, bool>> filter, params ISorting[] sortColumns);

    /// <summary>
    /// Get first ordered element by criteria.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="sortingColumns">The sorting columns.</param>
    /// <returns></returns>
    Task<TEntity> GetFirstAsync(Expression<Func<TEntity, bool>> filter, params ISorting[] sortingColumns);

    /// <summary>
    /// Get first ordered element by specification.
    /// </summary>
    /// <param name="specification">The specification.</param>
    /// <param name="sortingColumns">The sorting columns.</param>
    /// <returns></returns>
    Task<TEntity> GetFirstAsync(ISpecification<TEntity> specification, params ISorting[] sortingColumns);

    /// <summary>
    /// </summary>
    /// <param name="limit"></param>
    /// <param name="sortColumns"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(int limit, params ISorting[] sortColumns);

    /// <summary>
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="limit"></param>
    /// <param name="sortColumns"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(ISpecification<TEntity> specification, int limit, params ISorting[] sortColumns);

    /// <summary>
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="limit"></param>
    /// <param name="sortColumns"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(Expression<Func<TEntity, bool>> filter, int limit, params ISorting[] sortColumns);

    /// <summary>
    /// </summary>
    /// <param name="pageIndex"></param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="sortColumns"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(int pageIndex, int pageSize, params ISorting[] sortColumns);

    /// <summary>
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="pageIndex"></param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="sortColumns"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(ISpecification<TEntity> specification, int pageIndex, int pageSize, params ISorting[] sortColumns);

    /// <summary>
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="pageIndex"></param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="sortColumns"></param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(Expression<Func<TEntity, bool>> filter, int pageIndex, int pageSize, params ISorting[] sortColumns);

    /// <summary>
    /// Get single ordered element by criteria.
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="sortingColumns"></param>
    /// <returns></returns>
    Task<TEntity> GetSingleAsync(Expression<Func<TEntity, bool>> filter, params ISorting[] sortingColumns);

    /// <summary>
    /// Get single ordered element by specification.
    /// </summary>
    /// <param name="specification">The specification.</param>
    /// <param name="sortingColumns">The sorting columns.</param>
    /// <returns></returns>
    Task<TEntity> GetSingleAsync(ISpecification<TEntity> specification, params ISorting[] sortingColumns);
}

/// <summary>
/// The asynchronous read repository
/// </summary>
/// <typeparam name="TUnitOfWork">The type of the unit of work.</typeparam>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <seealso cref="eQuantic.Core.Data.Repository.IAsyncRepository" />
public interface IAsyncReadRepository<TUnitOfWork, TEntity, TKey> : IAsyncReadRepository<TEntity, TKey>, IAsyncRepository<TUnitOfWork>
    where TUnitOfWork : IUnitOfWork
    where TEntity : class, IEntity, new()
{
}