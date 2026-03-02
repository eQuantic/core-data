using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using eQuantic.Core.Data.Repository.Config;
using eQuantic.Linq.Specification;

namespace eQuantic.Core.Data.Repository.Read;

/// <summary>
/// The read repository interface
/// </summary>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
public interface IReadRepository<TEntity, in TKey> : IRepository
    where TEntity : class, IEntity, new()
{
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
    /// Computes the sum of the sequence of <see cref="int"/> values obtained by invoking a projection function on each element of the repository.
    /// </summary>
    /// <param name="source">A projection function to apply to each element.</param>
    /// <returns>The sum of the projected values.</returns>
    int Sum(Expression<Func<TEntity, int>> source);
    int Sum(ISpecification<TEntity> specification, Expression<Func<TEntity, int>> source);
    int Sum(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, int>> source);

    /// <summary>
    /// Computes the sum of the sequence of nullable <see cref="int"/> values obtained by invoking a projection function on each element of the repository.
    /// </summary>
    /// <param name="source">A projection function to apply to each element.</param>
    /// <returns>The sum of the projected values, or <c>null</c> if the sequence is empty or contains only <c>null</c> values.</returns>
    int? Sum(Expression<Func<TEntity, int?>> source);
    int? Sum(ISpecification<TEntity> specification, Expression<Func<TEntity, int?>> source);
    int? Sum(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, int?>> source);
    
    /// <summary>
    /// Computes the sum of the sequence of <see cref="long"/> values obtained by invoking a projection function on each element of the repository.
    /// </summary>
    /// <param name="source">A projection function to apply to each element.</param>
    /// <returns>The sum of the projected values.</returns>
    long Sum(Expression<Func<TEntity, long>> source);
    long Sum(ISpecification<TEntity> specification, Expression<Func<TEntity, long>> source);
    long Sum(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, long>> source);
    
    /// <summary>
    /// Computes the sum of the sequence of nullable <see cref="long"/> values obtained by invoking a projection function on each element of the repository.
    /// </summary>
    /// <param name="source">A projection function to apply to each element.</param>
    /// <returns>The sum of the projected values, or <c>null</c> if the sequence is empty or contains only <c>null</c> values.</returns>
    long? Sum(Expression<Func<TEntity, long?>> source);
    long? Sum(ISpecification<TEntity> specification, Expression<Func<TEntity, long?>> source);
    long? Sum(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, long?>> source);
    
    /// <summary>
    /// Computes the sum of the sequence of <see cref="double"/> values obtained by invoking a projection function on each element of the repository.
    /// </summary>
    /// <param name="source">A projection function to apply to each element.</param>
    /// <returns>The sum of the projected values.</returns>
    double Sum(Expression<Func<TEntity, double>> source);
    double Sum(ISpecification<TEntity> specification, Expression<Func<TEntity, double>> source);
    double Sum(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, double>> source);
    
    /// <summary>
    /// Computes the sum of the sequence of nullable <see cref="double"/> values obtained by invoking a projection function on each element of the repository.
    /// </summary>
    /// <param name="source">A projection function to apply to each element.</param>
    /// <returns>The sum of the projected values, or <c>null</c> if the sequence is empty or contains only <c>null</c> values.</returns>
    double? Sum(Expression<Func<TEntity, double?>> source);
    double? Sum(ISpecification<TEntity> specification, Expression<Func<TEntity, double?>> source);
    double? Sum(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, double?>> source);

    /// <summary>
    /// Computes the sum of the sequence of <see cref="float"/> values obtained by invoking a projection function on each element of the repository.
    /// </summary>
    /// <param name="source">A projection function to apply to each element.</param>
    /// <returns>The sum of the projected values.</returns>
    float Sum(Expression<Func<TEntity, float>> source);
    float Sum(ISpecification<TEntity> specification, Expression<Func<TEntity, float>> source);
    float Sum(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, float>> source);
    
    /// <summary>
    /// Computes the sum of the sequence of nullable <see cref="float"/> values obtained by invoking a projection function on each element of the repository.
    /// </summary>
    /// <param name="source">A projection function to apply to each element.</param>
    /// <returns>The sum of the projected values, or <c>null</c> if the sequence is empty or contains only <c>null</c> values.</returns>
    float? Sum(Expression<Func<TEntity, float?>> source);
    float? Sum(ISpecification<TEntity> specification, Expression<Func<TEntity, float?>> source);
    float? Sum(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, float?>> source);
    
    /// <summary>
    /// Computes the sum of the sequence of <see cref="decimal"/> values obtained by invoking a projection function on each element of the repository.
    /// </summary>
    /// <param name="source">A projection function to apply to each element.</param>
    /// <returns>The sum of the projected values.</returns>
    decimal Sum(Expression<Func<TEntity, decimal>> source);
    decimal Sum(ISpecification<TEntity> specification, Expression<Func<TEntity, decimal>> source);
    decimal Sum(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, decimal>> source);
    
    /// <summary>
    /// Computes the sum of the sequence of nullable <see cref="decimal"/> values obtained by invoking a projection function on each element of the repository.
    /// </summary>
    /// <param name="source">A projection function to apply to each element.</param>
    /// <returns>The sum of the projected values, or <c>null</c> if the sequence is empty or contains only <c>null</c> values.</returns>
    decimal? Sum(Expression<Func<TEntity, decimal?>> source);
    decimal? Sum(ISpecification<TEntity> specification, Expression<Func<TEntity, decimal?>> source);
    decimal? Sum(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, decimal?>> source);
}

/// <summary>
/// The read repository interface
/// </summary>
///
/// <typeparam name="TConfig"></typeparam>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <seealso cref="IRepository" />
public interface IReadRepository<out TConfig, TEntity, in TKey> : IReadRepository<TEntity, TKey>
    where TEntity : class, IEntity, new()
    where TConfig : Configuration<TEntity>
{
    /// <summary>
    /// Get all elements of type TEntity that matching a
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    IEnumerable<TEntity> AllMatching(ISpecification<TEntity> specification, Action<TConfig> configuration = default);

    /// <summary>
    /// Verify all specified elements of type TEntity in repository
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    bool All(ISpecification<TEntity> specification, Action<TConfig> configuration = default);

    /// <summary>
    /// Verify all filtered elements of type TEntity in repository
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    bool All(Expression<Func<TEntity, bool>> filter, Action<TConfig> configuration = default);
    
    /// <summary>
    /// Verify any elements of type TEntity in repository
    /// </summary>
    /// <returns></returns>
    bool Any(Action<TConfig> configuration = default);

    /// <summary>
    /// Verify any specified elements of type TEntity in repository
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    bool Any(ISpecification<TEntity> specification, Action<TConfig> configuration = default);

    /// <summary>
    /// Verify any filtered elements of type TEntity in repository
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    bool Any(Expression<Func<TEntity, bool>> filter, Action<TConfig> configuration = default);
    
    /// <summary>
    /// Get element by entity key
    /// </summary>
    /// <param name="id"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    TEntity Get(TKey id, Action<TConfig> configuration = default);

    /// <summary>
    /// Get all elements of type TEntity in repository
    /// </summary>
    /// <param name="configuration"></param>
    /// <returns></returns>
    IEnumerable<TEntity> GetAll(Action<TConfig> configuration = default);

    /// <summary>
    /// Get mapped elements from TEntity type by criteria
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="map"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    IEnumerable<TResult> GetMapped<TResult>(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TResult>> map, Action<TConfig> configuration = default);
    
    /// <summary>
    /// Get mapped elements from TEntity type by specification
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="map"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    IEnumerable<TResult> GetMapped<TResult>(ISpecification<TEntity> specification, Expression<Func<TEntity, TResult>> map, Action<TConfig> configuration = default);
    
    /// <summary>
    /// Get all elements by criteria
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    IEnumerable<TEntity> GetFiltered(Expression<Func<TEntity, bool>> filter, Action<TConfig> configuration = default);

    /// <summary>
    /// Get first element by criteria
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    TEntity GetFirst(Expression<Func<TEntity, bool>> filter, Action<TConfig> configuration = default);

    /// <summary>
    /// Get first element by specification.
    /// </summary>
    /// <param name="specification">The specification.</param>
    /// <param name="configuration">Configure the source.</param>
    /// <returns></returns>
    TEntity GetFirst(ISpecification<TEntity> specification, Action<TConfig> configuration = default);

    /// <summary>
    /// Get first mapped element by criteria
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="map"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    TResult GetFirstMapped<TResult>(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TResult>> map, Action<TConfig> configuration = default);

    /// <summary>
    /// Get first mapped element by specification.
    /// </summary>
    /// <param name="specification">The specification.</param>
    /// <param name="map"></param>
    /// <param name="configuration">Configure the source.</param>
    /// <returns></returns>
    TResult GetFirstMapped<TResult>(ISpecification<TEntity> specification, Expression<Func<TEntity, TResult>> map, Action<TConfig> configuration = default);
    
    /// <summary>
    /// Get paged elements
    /// </summary>
    /// <param name="limit"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    IEnumerable<TEntity> GetPaged(int limit, Action<TConfig> configuration = default);

    /// <summary>
    /// Get paged elements by specification
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="limit"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    IEnumerable<TEntity> GetPaged(ISpecification<TEntity> specification, int limit, Action<TConfig> configuration = default);

    /// <summary>
    /// Get paged elements by criteria
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="limit"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    IEnumerable<TEntity> GetPaged(Expression<Func<TEntity, bool>> filter, int limit, Action<TConfig> configuration = default);

    /// <summary>
    /// Get paged elements
    /// </summary>
    /// <param name="pageIndex"></param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    IEnumerable<TEntity> GetPaged(int pageIndex, int pageSize, Action<TConfig> configuration = default);

    /// <summary>
    /// Get paged elements by specification
    /// </summary>
    /// <param name="specification"></param>
    /// <param name="pageIndex"></param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    IEnumerable<TEntity> GetPaged(ISpecification<TEntity> specification, int pageIndex, int pageSize, Action<TConfig> configuration = default);

    /// <summary>
    /// Get paged elements by criteria
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="pageIndex"></param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    IEnumerable<TEntity> GetPaged(Expression<Func<TEntity, bool>> filter, int pageIndex, int pageSize, Action<TConfig> configuration = default);

    /// <summary>
    /// Get single element by criteria
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    TEntity GetSingle(Expression<Func<TEntity, bool>> filter, Action<TConfig> configuration = default);

    /// <summary>
    /// Get single element by specification.
    /// </summary>
    /// <param name="specification">The specification.</param>
    /// <param name="configuration">The sort columns.</param>
    /// <returns></returns>
    TEntity GetSingle(ISpecification<TEntity> specification, Action<TConfig> configuration = default);
}

/// <summary>
/// The read repository interface
/// </summary>
/// <typeparam name="TUnitOfWork">The type of the unit of work.</typeparam>
/// <typeparam name="TConfig"></typeparam>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <seealso cref="IRepository" />
public interface IReadRepository<out TUnitOfWork, out TConfig, TEntity, in TKey> : IReadRepository<TConfig, TEntity, TKey>, IRepository<TUnitOfWork>
    where TUnitOfWork : IUnitOfWork
    where TEntity : class, IEntity, new()
    where TConfig : Configuration<TEntity>
{
}
