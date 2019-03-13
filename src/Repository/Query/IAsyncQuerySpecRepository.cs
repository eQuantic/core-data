using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading.Tasks;
using eQuantic.Core.Linq;
using eQuantic.Core.Linq.Specification;

namespace eQuantic.Core.Data.Repository.Query
{
    public interface IAsyncQuerySpecRepository<TEntity, TKey> : IAsyncQueryRepository<TEntity, TKey>
        where TEntity : class, IEntity, new()
    {
        /// <summary>
        /// Get all elements of type TEntity that matching a Specification <paramref name="specification"/>
        /// </summary>
        /// <param name="specification">Specification that result meet</param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<IEnumerable<TEntity>> AllMatchingAsync(ISpecification<TEntity> specification, params string[] loadProperties);

        /// <summary>
        /// </summary>
        /// <param name="specification"></param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<IEnumerable<TEntity>> AllMatchingAsync(ISpecification<TEntity> specification,
            params Expression<Func<TEntity, object>>[] loadProperties);

        /// <summary>
        /// Get all elements of type TEntity in repository
        /// </summary>
        /// <returns>List of selected elements</returns>
        Task<IEnumerable<TEntity>> GetAllAsync(params string[] loadProperties);

        /// <summary>
        /// </summary>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<IEnumerable<TEntity>> GetAllAsync(params Expression<Func<TEntity, object>>[] loadProperties);

        /// <summary>
        /// Get all elements of type TEntity in repository
        /// </summary>
        /// <param name="sortingColumns"></param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<IEnumerable<TEntity>> GetAllAsync(ISorting[] sortingColumns, params string[] loadProperties);

        /// <summary>
        /// </summary>
        /// <param name="sortingColumns"></param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<IEnumerable<TEntity>> GetAllAsync(ISorting[] sortingColumns,
            params Expression<Func<TEntity, object>>[] loadProperties);

        /// <summary>
        /// Get element by entity key
        /// </summary>
        /// <param name="id"></param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<TEntity> GetAsync(TKey id, params string[] loadProperties);

        /// <summary>
        /// Get element by entity key
        /// </summary>
        /// <param name="id"></param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<TEntity> GetAsync(TKey id, params Expression<Func<TEntity, object>>[] loadProperties);

        /// <summary>
        /// Get elements of type TEntity in repository
        /// </summary>
        /// <param name="filter">Filter that each element do match</param>
        /// <param name="loadProperties">Load properties from element</param>
        /// <returns>List of selected elements</returns>
        Task<IEnumerable<TEntity>> GetFilteredAsync(Expression<Func<TEntity, bool>> filter, params string[] loadProperties);

        /// <summary>
        /// </summary>
        /// <param name="filter"></param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<IEnumerable<TEntity>> GetFilteredAsync(Expression<Func<TEntity, bool>> filter,
            params Expression<Func<TEntity, object>>[] loadProperties);

        /// <summary>
        /// </summary>
        /// <param name="filter"></param>
        /// <param name="sortColumns"></param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<IEnumerable<TEntity>> GetFilteredAsync(Expression<Func<TEntity, bool>> filter, ISorting[] sortColumns,
            params string[] loadProperties);

        /// <summary>
        /// </summary>
        /// <param name="filter"></param>
        /// <param name="sortColumns"></param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<IEnumerable<TEntity>> GetFilteredAsync(Expression<Func<TEntity, bool>> filter, ISorting[] sortColumns,
            params Expression<Func<TEntity, object>>[] loadProperties);

        /// <summary>
        /// Get first element by criteria
        /// </summary>
        /// <param name="filter">Filter that each element do match</param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<TEntity> GetFirstAsync(Expression<Func<TEntity, bool>> filter, params string[] loadProperties);

        /// <summary>
        /// </summary>
        /// <param name="filter"></param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<TEntity> GetFirstAsync(Expression<Func<TEntity, bool>> filter,
            params Expression<Func<TEntity, object>>[] loadProperties);

        /// <summary>
        /// </summary>
        /// <param name="filter"></param>
        /// <param name="sortingColumns"></param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<TEntity> GetFirstAsync(Expression<Func<TEntity, bool>> filter, ISorting[] sortingColumns, params string[] loadProperties);

        /// <summary>
        ///
        /// </summary> <param name="filter"></param> <param name="sortingColumns"></param> <param
        /// name="Expression<Func<TEntity"></param> <param name="loadProperties"></param> <returns></returns>
        Task<TEntity> GetFirstAsync(Expression<Func<TEntity, bool>> filter, ISorting[] sortingColumns, params Expression<Func<TEntity, object>>[] loadProperties);

        /// <summary>
        /// </summary>
        /// <param name="limit"></param>
        /// <param name="sortColumns"></param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<IEnumerable<TEntity>> GetPagedAsync(int limit, ISorting[] sortColumns, params string[] loadProperties);

        /// <summary>
        /// </summary>
        /// <param name="limit"></param>
        /// <param name="sortColumns"></param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<IEnumerable<TEntity>> GetPagedAsync(int limit, ISorting[] sortColumns, params Expression<Func<TEntity, object>>[] loadProperties);

        /// <summary>
        /// </summary>
        /// <param name="specification"></param>
        /// <param name="limit"></param>
        /// <param name="sortColumns"></param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<IEnumerable<TEntity>> GetPagedAsync(ISpecification<TEntity> specification, int limit,
            ISorting[] sortColumns, params string[] loadProperties);

        /// <summary>
        /// </summary>
        /// <param name="specification"></param>
        /// <param name="limit"></param>
        /// <param name="sortColumns"></param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<IEnumerable<TEntity>> GetPagedAsync(ISpecification<TEntity> specification, int limit,
            ISorting[] sortColumns, params Expression<Func<TEntity, object>>[] loadProperties);

        /// <summary>
        /// </summary>
        /// <param name="filter"></param>
        /// <param name="limit"></param>
        /// <param name="sortColumns"></param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<IEnumerable<TEntity>> GetPagedAsync(Expression<Func<TEntity, bool>> filter, int limit,
            ISorting[] sortColumns, params string[] loadProperties);

        /// <summary>
        /// </summary>
        /// <param name="filter"></param>
        /// <param name="limit"></param>
        /// <param name="sortColumns"></param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<IEnumerable<TEntity>> GetPagedAsync(Expression<Func<TEntity, bool>> filter, int limit,
            ISorting[] sortColumns, params Expression<Func<TEntity, object>>[] loadProperties);

        /// <summary>
        /// </summary>
        /// <param name="pageIndex"></param>
        /// <param name="pageCount"></param>
        /// <param name="sortColumns"></param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<IEnumerable<TEntity>> GetPagedAsync(int pageIndex, int pageCount, ISorting[] sortColumns,
            params string[] loadProperties);

        /// <summary>
        /// </summary>
        /// <param name="pageIndex"></param>
        /// <param name="pageCount"></param>
        /// <param name="sortColumns"></param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<IEnumerable<TEntity>> GetPagedAsync(int pageIndex, int pageCount, ISorting[] sortColumns,
            params Expression<Func<TEntity, object>>[] loadProperties);

        /// <summary>
        /// Get all elements of type TEntity in repository
        /// </summary>
        /// <param name="specification"></param>
        /// <param name="pageIndex"></param>
        /// <param name="pageCount"></param>
        /// <param name="sortColumns"></param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<IEnumerable<TEntity>> GetPagedAsync(ISpecification<TEntity> specification, int pageIndex, int pageCount,
            ISorting[] sortColumns, params string[] loadProperties);

        /// <summary>
        /// </summary>
        /// <param name="specification"></param>
        /// <param name="pageIndex"></param>
        /// <param name="pageCount"></param>
        /// <param name="sortColumns"></param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<IEnumerable<TEntity>> GetPagedAsync(ISpecification<TEntity> specification, int pageIndex, int pageCount,
            ISorting[] sortColumns, params Expression<Func<TEntity, object>>[] loadProperties);

        /// <summary>
        /// Get all elements of type TEntity in repository
        /// </summary>
        /// <param name="filter"></param>
        /// <param name="pageIndex"></param>
        /// <param name="pageCount"></param>
        /// <param name="sortColumns"></param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<IEnumerable<TEntity>> GetPagedAsync(Expression<Func<TEntity, bool>> filter, int pageIndex, int pageCount,
            ISorting[] sortColumns, params string[] loadProperties);

        /// <summary>
        /// </summary>
        /// <param name="filter"></param>
        /// <param name="pageIndex"></param>
        /// <param name="pageCount"></param>
        /// <param name="sortColumns"></param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<IEnumerable<TEntity>> GetPagedAsync(Expression<Func<TEntity, bool>> filter, int pageIndex, int pageCount,
            ISorting[] sortColumns, params Expression<Func<TEntity, object>>[] loadProperties);

        /// <summary>
        /// Get single element by criteria
        /// </summary>
        /// <param name="filter">Filter that each element do match</param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<TEntity> GetSingleAsync(Expression<Func<TEntity, bool>> filter, params string[] loadProperties);

        /// <summary>
        /// </summary>
        /// <param name="filter"></param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<TEntity> GetSingleAsync(Expression<Func<TEntity, bool>> filter,
            params Expression<Func<TEntity, object>>[] loadProperties);

        /// <summary>
        /// </summary>
        /// <param name="filter"></param>
        /// <param name="sortingColumns"></param>
        /// <param name="loadProperties"></param>
        /// <returns></returns>
        Task<TEntity> GetSingleAsync(Expression<Func<TEntity, bool>> filter, ISorting[] sortingColumns,
            params string[] loadProperties);

        /// <summary>
        ///
        /// </summary> <param name="filter"></param> <param name="sortingColumns"></param> <param
        /// name="Expression<Func<TEntity"></param> <param name="loadProperties"></param> <returns></returns>
        Task<TEntity> GetSingleAsync(Expression<Func<TEntity, bool>> filter, ISorting[] sortingColumns,
            params Expression<Func<TEntity, object>>[] loadProperties);
    }

    public interface IAsyncQuerySpecRepository<TUnitOfWork, TEntity, TKey> : IAsyncQuerySpecRepository<TEntity, TKey>, IAsyncRepository<TUnitOfWork>
        where TUnitOfWork : IUnitOfWork
        where TEntity : class, IEntity, new()
    {
    }
}