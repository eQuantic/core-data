using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using eQuantic.Core.Data.Repository.Config;
using eQuantic.Linq.Sorter;
using eQuantic.Linq.Specification;

namespace eQuantic.Core.Data.Repository.Read;

/// <summary>
/// The asynchronous read repository with specification pattern
/// </summary>
/// <typeparam name="TUnitOfWork">The type of the unit of work.</typeparam>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <seealso cref="eQuantic.Core.Data.Repository.Read.IAsyncReadRepository{TEntity, TKey}" />
public interface IAsyncQueryableReadRepository<out TUnitOfWork, TEntity, TKey> : IAsyncQueryableReadRepository<TEntity, TKey>, IAsyncRepository<TUnitOfWork>
    where TUnitOfWork : IUnitOfWork
    where TEntity : class, IEntity, new()
{
}

/// <summary>
/// The asynchronous read repository with specification pattern
/// </summary>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <seealso cref="eQuantic.Core.Data.Repository.Read.IAsyncReadRepository{TEntity, TKey}" />
public interface IAsyncQueryableReadRepository<TEntity, TKey> : IAsyncReadRepository<TEntity, TKey>
    where TEntity : class, IEntity, new()
{
    /// <summary>
    /// Get all elements of type TEntity that matching a Specification <paramref name="specification"/>
    /// </summary>
    /// <param name="specification">Specification that result meet</param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> AllMatchingAsync(ISpecification<TEntity> specification, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Get all elements of type TEntity in repository
    /// </summary>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns>List of selected elements</returns>
    Task<IEnumerable<TEntity>> GetAllAsync(Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Get all elements of type TEntity in repository
    /// </summary>
    /// <param name="sortingColumns"></param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetAllAsync(ISorting[] sortingColumns, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Get element by entity key
    /// </summary>
    /// <param name="id"></param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns>The entity</returns>
    Task<TEntity> GetAsync(TKey id, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Get elements of type TEntity in repository
    /// </summary>
    /// <param name="filter">Filter that each element do match</param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns>List of selected elements</returns>
    Task<IEnumerable<TEntity>> GetFilteredAsync(Expression<Func<TEntity, bool>> filter, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="sortColumns"></param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetFilteredAsync(Expression<Func<TEntity, bool>> filter, ISorting[] sortColumns, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Get first element by criteria.
    /// </summary>
    /// <param name="filter">Filter that each element do match</param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    Task<TEntity> GetFirstAsync(Expression<Func<TEntity, bool>> filter, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Get first element by specification.
    /// </summary>
    /// <param name="specification">The specification.</param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    Task<TEntity> GetFirstAsync(ISpecification<TEntity> specification, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Get first ordered element by criteria.
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="sortingColumns"></param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    Task<TEntity> GetFirstAsync(Expression<Func<TEntity, bool>> filter, ISorting[] sortingColumns, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Get first ordered element by specification.
    /// </summary>
    /// <param name="specification">The specification.</param>
    /// <param name="sortingColumns">The sorting columns.</param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    Task<TEntity> GetFirstAsync(ISpecification<TEntity> specification, ISorting[] sortingColumns, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// </summary>
    /// <param name="limit"></param>
    /// <param name="sortColumns"></param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(int limit, ISorting[] sortColumns, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="limit"></param>
    /// <param name="sortColumns"></param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(ISpecification<TEntity> specification, int limit, ISorting[] sortColumns, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="limit"></param>
    /// <param name="sortColumns"></param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(Expression<Func<TEntity, bool>> filter, int limit, ISorting[] sortColumns, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// </summary>
    /// <param name="pageIndex"></param>
    /// <param name="pageSize"></param>
    /// <param name="sortColumns"></param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(int pageIndex, int pageSize, ISorting[] sortColumns, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Get all elements of type TEntity in repository
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="pageIndex"></param>
    /// <param name="pageSize"></param>
    /// <param name="sortColumns"></param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(ISpecification<TEntity> specification, int pageIndex, int pageSize, ISorting[] sortColumns, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Get all elements of type TEntity in repository
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="pageIndex"></param>
    /// <param name="pageSize"></param>
    /// <param name="sortColumns"></param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    Task<IEnumerable<TEntity>> GetPagedAsync(Expression<Func<TEntity, bool>> filter, int pageIndex, int pageSize, ISorting[] sortColumns, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Get single element by criteria
    /// </summary>
    /// <param name="filter">Filter that each element do match</param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    Task<TEntity> GetSingleAsync(Expression<Func<TEntity, bool>> filter, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Gets the single asynchronous.
    /// </summary>
    /// <param name="specification">The specification.</param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    Task<TEntity> GetSingleAsync(ISpecification<TEntity> specification, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Get single element asynchronously by criteria
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="sortingColumns"></param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    Task<TEntity> GetSingleAsync(Expression<Func<TEntity, bool>> filter, ISorting[] sortingColumns, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Gets the single asynchronous.
    /// </summary>
    /// <param name="specification">The specification.</param>
    /// <param name="sortingColumns">The sorting columns.</param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    Task<TEntity> GetSingleAsync(ISpecification<TEntity> specification, ISorting[] sortingColumns, Action<QueryableConfiguration<TEntity>> configuration);
}