using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using eQuantic.Linq.Sorter;
using eQuantic.Linq.Specification;

namespace eQuantic.Core.Data.Repository.Read;

/// <summary>
/// The read repository interface
/// </summary>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <seealso cref="eQuantic.Core.Data.Repository.IRepository" />
public interface IReadRepository<TEntity, TKey> : IRepository
    where TEntity : class, IEntity, new()
{
    /// <summary>
    /// Get all elements of type TEntity that matching a
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="sortingColumns"></param>
    /// <returns></returns>
    IEnumerable<TEntity> AllMatching(ISpecification<TEntity> specification, params ISorting[] sortingColumns);

    /// <summary>
    /// Count elements of type TEntity in repository
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
    /// <param name="sortingColumns"></param>
    /// <returns></returns>
    IEnumerable<TEntity> GetAll(params ISorting[] sortingColumns);

    /// <summary>
    /// Get all elements by criteria
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="sortColumns"></param>
    /// <returns></returns>
    IEnumerable<TEntity> GetFiltered(Expression<Func<TEntity, bool>> filter, params ISorting[] sortColumns);

    /// <summary>
    /// Get first element by criteria
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="sortColumns"></param>
    /// <returns></returns>
    TEntity GetFirst(Expression<Func<TEntity, bool>> filter, params ISorting[] sortColumns);

    /// <summary>
    /// Get first element by specification.
    /// </summary>
    /// <param name="specification">The specification.</param>
    /// <param name="sortColumns">The sort columns.</param>
    /// <returns></returns>
    TEntity GetFirst(ISpecification<TEntity> specification, params ISorting[] sortColumns);

    /// <summary>
    /// Get paged elements
    /// </summary>
    /// <param name="limit"></param>
    /// <param name="sortColumns"></param>
    /// <returns></returns>
    IEnumerable<TEntity> GetPaged(int limit, params ISorting[] sortColumns);

    /// <summary>
    /// Get paged elements by specification
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="limit"></param>
    /// <param name="sortColumns"></param>
    /// <returns></returns>
    IEnumerable<TEntity> GetPaged(ISpecification<TEntity> specification, int limit, params ISorting[] sortColumns);

    /// <summary>
    /// Get paged elements by criteria
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="limit"></param>
    /// <param name="sortColumns"></param>
    /// <returns></returns>
    IEnumerable<TEntity> GetPaged(Expression<Func<TEntity, bool>> filter, int limit, params ISorting[] sortColumns);

    /// <summary>
    /// Get paged elements
    /// </summary>
    /// <param name="pageIndex"></param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="sortColumns"></param>
    /// <returns></returns>
    IEnumerable<TEntity> GetPaged(int pageIndex, int pageSize, params ISorting[] sortColumns);

    /// <summary>
    /// Get paged elements by specification
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="pageIndex"></param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="sortColumns"></param>
    /// <returns></returns>
    IEnumerable<TEntity> GetPaged(ISpecification<TEntity> specification, int pageIndex, int pageSize, params ISorting[] sortColumns);

    /// <summary>
    /// Get paged elements by criteria
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="pageIndex"></param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="sortColumns"></param>
    /// <returns></returns>
    IEnumerable<TEntity> GetPaged(Expression<Func<TEntity, bool>> filter, int pageIndex, int pageSize, params ISorting[] sortColumns);

    /// <summary>
    /// Get single element by criteria
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="sortColumns"></param>
    /// <returns></returns>
    TEntity GetSingle(Expression<Func<TEntity, bool>> filter, params ISorting[] sortColumns);

    /// <summary>
    /// Get single element by specification.
    /// </summary>
    /// <param name="specification">The specification.</param>
    /// <param name="sortColumns">The sort columns.</param>
    /// <returns></returns>
    TEntity GetSingle(ISpecification<TEntity> specification, params ISorting[] sortColumns);
}

/// <summary>
/// The read repository interface
/// </summary>
/// <typeparam name="TUnitOfWork">The type of the unit of work.</typeparam>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <seealso cref="eQuantic.Core.Data.Repository.IRepository" />
public interface IReadRepository<TUnitOfWork, TEntity, TKey> : IReadRepository<TEntity, TKey>, IRepository<TUnitOfWork>
    where TUnitOfWork : IUnitOfWork
    where TEntity : class, IEntity, new()
{
}