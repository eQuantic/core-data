using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using eQuantic.Core.Linq;
using eQuantic.Core.Linq.Specification;

namespace eQuantic.Core.Data.Repository.Query
{
    public interface IQueryRepository<TEntity, TKey> : IRepository
        where TEntity : class, IEntity, new()
    {
        /// <summary>
        /// Get all elements of type TEntity that matching a
        /// </summary>
        /// <param name="specification"></param>
        /// <returns></returns>
        IEnumerable<TEntity> AllMatching(ISpecification<TEntity> specification);

        /// <summary>
        /// </summary>
        /// <returns></returns>
        long Count();

        /// <summary>
        /// Count specified elements of type TEntity in repository
        /// </summary>
        /// <param name="specification"></param>
        /// <returns></returns>
        long Count(ISpecification<TEntity> specification);

        /// <summary>
        /// Count filtered elements of type TEntity in repository
        /// </summary>
        /// <param name="filter"></param>
        /// <returns></returns>
        long Count(Expression<Func<TEntity, bool>> filter);

        /// <summary>
        /// Get element by entity key
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        TEntity Get(TKey id);

        /// <summary>
        /// Get all elements of type TEntity in repository
        /// </summary>
        /// <returns></returns>
        IEnumerable<TEntity> GetAll();

        /// <summary>
        /// Get all elements of type TEntity in repository
        /// </summary>
        /// <param name="sortingColumns"></param>
        /// <returns></returns>
        IEnumerable<TEntity> GetAll(ISorting[] sortingColumns);

        /// <summary>
        /// </summary>
        /// <param name="filter"></param>
        /// <returns></returns>
        IEnumerable<TEntity> GetFiltered(Expression<Func<TEntity, bool>> filter);

        /// <summary>
        /// </summary>
        /// <param name="filter"></param>
        /// <param name="sortColumns"></param>
        /// <returns></returns>
        IEnumerable<TEntity> GetFiltered(Expression<Func<TEntity, bool>> filter, ISorting[] sortColumns);

        /// <summary>
        /// Get first element by criteria
        /// </summary>
        /// <param name="filter"></param>
        /// <returns></returns>
        TEntity GetFirst(Expression<Func<TEntity, bool>> filter);

        /// <summary>
        /// </summary>
        /// <param name="limit"></param>
        /// <param name="sortColumns"></param>
        /// <returns></returns>
        IEnumerable<TEntity> GetPaged(int limit, ISorting[] sortColumns);

        /// <summary>
        /// </summary>
        /// <param name="specification"></param>
        /// <param name="limit"></param>
        /// <param name="sortColumns"></param>
        /// <returns></returns>
        IEnumerable<TEntity> GetPaged(ISpecification<TEntity> specification, int limit,
            ISorting[] sortColumns);

        /// <summary>
        /// </summary>
        /// <param name="filter"></param>
        /// <param name="limit"></param>
        /// <param name="sortColumns"></param>
        /// <returns></returns>
        IEnumerable<TEntity> GetPaged(Expression<Func<TEntity, bool>> filter, int limit,
            ISorting[] sortColumns);

        /// <summary>
        /// </summary>
        /// <param name="pageIndex"></param>
        /// <param name="pageCount"></param>
        /// <param name="sortColumns"></param>
        /// <returns></returns>
        IEnumerable<TEntity> GetPaged(int pageIndex, int pageCount, ISorting[] sortColumns);

        /// <summary>
        /// </summary>
        /// <param name="specification"></param>
        /// <param name="pageIndex"></param>
        /// <param name="pageCount"></param>
        /// <param name="sortColumns"></param>
        /// <returns></returns>
        IEnumerable<TEntity> GetPaged(ISpecification<TEntity> specification, int pageIndex, int pageCount,
            ISorting[] sortColumns);

        /// <summary>
        /// </summary>
        /// <param name="filter"></param>
        /// <param name="pageIndex"></param>
        /// <param name="pageCount"></param>
        /// <param name="sortColumns"></param>
        /// <returns></returns>
        IEnumerable<TEntity> GetPaged(Expression<Func<TEntity, bool>> filter, int pageIndex, int pageCount,
            ISorting[] sortColumns);

        /// <summary>
        /// Get single element by criteria
        /// </summary>
        /// <param name="filter"></param>
        /// <returns></returns>
        TEntity GetSingle(Expression<Func<TEntity, bool>> filter);
    }

    public interface IQueryRepository<TUnitOfWork, TEntity, TKey> : IQueryRepository<TEntity, TKey>, IRepository<TUnitOfWork>
        where TUnitOfWork : IUnitOfWork
        where TEntity : class, IEntity, new()
    {
    }
}