using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using eQuantic.Core.Data.Repository.Config;
using eQuantic.Linq.Sorter;
using eQuantic.Linq.Specification;

namespace eQuantic.Core.Data.Repository.Read;

/// <summary>
/// The read repository with joins
/// </summary>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <seealso cref="eQuantic.Core.Data.Repository.Read.IReadRepository{TEntity, TKey}" />
public interface IQueryableReadRepository<TEntity, TKey> : IReadRepository<TEntity, TKey>
    where TEntity : class, IEntity, new()
{
    /// <summary>
    /// Get all elements of type TEntity that matching a Specification <paramref name="specification"/>
    /// </summary>
    /// <param name="specification">Specification that result meet</param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    IEnumerable<TEntity> AllMatching(ISpecification<TEntity> specification, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Get all elements of type TEntity that matching a Specification <paramref name="specification"/>
    /// </summary>
    /// <param name="specification">The specification.</param>
    /// <param name="sortingColumns">The sorting columns.</param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    IEnumerable<TEntity> AllMatching(ISpecification<TEntity> specification, ISorting[] sortingColumns, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Get element by entity key
    /// </summary>
    /// <param name="id"></param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    TEntity Get(TKey id, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Get all elements of type TEntity in repository
    /// </summary>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns>List of selected elements</returns>
    IEnumerable<TEntity> GetAll(Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Get all elements of type TEntity in repository
    /// </summary>
    /// <param name="sortingColumns"></param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    IEnumerable<TEntity> GetAll(ISorting[] sortingColumns, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Get elements of type TEntity in repository
    /// </summary>
    /// <param name="filter">Filter that each element do match</param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns>List of selected elements</returns>
    IEnumerable<TEntity> GetFiltered(Expression<Func<TEntity, bool>> filter, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="sortColumns"></param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    IEnumerable<TEntity> GetFiltered(Expression<Func<TEntity, bool>> filter, ISorting[] sortColumns, Action<QueryableConfiguration<TEntity>> configuration);
    
    /// <summary>
    /// Get first element by criteria
    /// </summary>
    /// <param name="filter">Filter that each element do match</param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    TEntity GetFirst(Expression<Func<TEntity, bool>> filter, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Gets the first.
    /// </summary>
    /// <param name="specification">The specification.</param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    TEntity GetFirst(ISpecification<TEntity> specification, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="sortingColumns"></param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    TEntity GetFirst(Expression<Func<TEntity, bool>> filter, ISorting[] sortingColumns, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Gets the first.
    /// </summary>
    /// <param name="specification">The specification.</param>
    /// <param name="sortingColumns">The sorting columns.</param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    TEntity GetFirst(ISpecification<TEntity> specification, ISorting[] sortingColumns, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// </summary>
    /// <param name="limit"></param>
    /// <param name="sortColumns"></param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    IEnumerable<TEntity> GetPaged(int limit, ISorting[] sortColumns, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="limit"></param>
    /// <param name="sortColumns"></param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    IEnumerable<TEntity> GetPaged(ISpecification<TEntity> specification, int limit, ISorting[] sortColumns, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="limit"></param>
    /// <param name="sortColumns"></param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    IEnumerable<TEntity> GetPaged(Expression<Func<TEntity, bool>> filter, int limit, ISorting[] sortColumns, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// </summary>
    /// <param name="pageIndex"></param>
    /// <param name="pageSize"></param>
    /// <param name="sortColumns"></param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    IEnumerable<TEntity> GetPaged(int pageIndex, int pageSize, ISorting[] sortColumns, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Get all elements of type TEntity in repository
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="pageIndex"></param>
    /// <param name="pageSize"></param>
    /// <param name="sortColumns"></param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    IEnumerable<TEntity> GetPaged(ISpecification<TEntity> specification, int pageIndex, int pageSize, ISorting[] sortColumns, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Get all elements of type TEntity in repository
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="pageIndex"></param>
    /// <param name="pageSize"></param>
    /// <param name="sortColumns"></param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    IEnumerable<TEntity> GetPaged(Expression<Func<TEntity, bool>> filter, int pageIndex, int pageSize, ISorting[] sortColumns, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Get single element by criteria
    /// </summary>
    /// <param name="filter">Filter that each element do match</param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    TEntity GetSingle(Expression<Func<TEntity, bool>> filter, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Gets the single.
    /// </summary>
    /// <param name="specification">The specification.</param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    TEntity GetSingle(ISpecification<TEntity> specification, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Gets the single.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="sortingColumns">The sorting columns.</param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    TEntity GetSingle(Expression<Func<TEntity, bool>> filter, ISorting[] sortingColumns, Action<QueryableConfiguration<TEntity>> configuration);

    /// <summary>
    /// Gets the single.
    /// </summary>
    /// <param name="specification">The specification.</param>
    /// <param name="sortingColumns">The sorting columns.</param>
    /// <param name="configuration">Configure queryable source.</param>
    /// <returns></returns>
    TEntity GetSingle(ISpecification<TEntity> specification, ISorting[] sortingColumns, Action<QueryableConfiguration<TEntity>> configuration);
}

/// <summary>
/// The read repository with joins
/// </summary>
/// <typeparam name="TUnitOfWork">The type of the unit of work.</typeparam>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <seealso cref="eQuantic.Core.Data.Repository.Read.IReadRepository{TEntity, TKey}" />
public interface IQueryableReadRepository<TUnitOfWork, TEntity, TKey> : IQueryableReadRepository<TEntity, TKey>, IRepository<TUnitOfWork>
    where TUnitOfWork : IUnitOfWork
    where TEntity : class, IEntity, new()
{
}