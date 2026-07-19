using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Linq.Specification;

namespace eQuantic.Core.Data.Repository.Read;

/// <summary>
/// The asynchronous read repository. Query shaping (filtering, sorting, includes,
/// tracking) is expressed through a single <see cref="QueryOptions{TEntity}"/>
/// argument, and paged reads return a <see cref="PagedResult{T}"/>.
/// </summary>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <seealso cref="IAsyncRepository" />
public interface IAsyncReadRepository<TEntity, TKey> : IAsyncRepository
    where TEntity : class, IEntity<TKey>
{
    /// <summary>
    /// Gets the element identified by its key.
    /// </summary>
    /// <param name="id">The entity key.</param>
    /// <param name="options">Optional query shaping.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The entity, or <c>null</c> when not found.</returns>
    Task<TEntity?> GetAsync(TKey id, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all elements, optionally shaped by <paramref name="options"/>.
    /// </summary>
    /// <param name="options">Optional query shaping (filter, sorting, includes, tracking).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The matching entities.</returns>
    Task<IEnumerable<TEntity>> GetAllAsync(QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all elements matching the supplied predicate.
    /// </summary>
    /// <param name="filter">The predicate to apply.</param>
    /// <param name="options">Optional additional query shaping.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The matching entities.</returns>
    Task<IEnumerable<TEntity>> GetFilteredAsync(Expression<Func<TEntity, bool>> filter, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all elements matching the supplied specification.
    /// </summary>
    /// <param name="specification">The specification to apply.</param>
    /// <param name="options">Optional additional query shaping.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The matching entities.</returns>
    Task<IEnumerable<TEntity>> AllMatchingAsync(ISpecification<TEntity> specification, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all elements projected to <typeparamref name="TResult"/>.
    /// </summary>
    /// <typeparam name="TResult">The projection result type.</typeparam>
    /// <param name="map">The projection to apply.</param>
    /// <param name="options">Optional query shaping (filter, sorting, includes, tracking).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The projected results.</returns>
    Task<IEnumerable<TResult>> GetMappedAsync<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the first element matching the supplied options, or <c>null</c> when none match.
    /// </summary>
    /// <param name="options">The query shaping, including the filter to apply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The first matching entity, or <c>null</c>.</returns>
    Task<TEntity?> GetFirstAsync(QueryOptions<TEntity> options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the first element matching the supplied options, projected to <typeparamref name="TResult"/>.
    /// </summary>
    /// <typeparam name="TResult">The projection result type.</typeparam>
    /// <param name="map">The projection to apply.</param>
    /// <param name="options">The query shaping, including the filter to apply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The first projected result, or the default when none match.</returns>
    Task<TResult?> GetFirstMappedAsync<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity> options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the single element matching the supplied options, or <c>null</c> when none match.
    /// </summary>
    /// <param name="options">The query shaping, including the filter to apply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The single matching entity, or <c>null</c>.</returns>
    Task<TEntity?> GetSingleAsync(QueryOptions<TEntity> options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a page of elements together with the total number of matching items.
    /// </summary>
    /// <param name="page">The page to retrieve.</param>
    /// <param name="options">Optional query shaping (filter, sorting, includes, tracking).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The requested page of entities.</returns>
    Task<PagedResult<TEntity>> GetPagedAsync(PageRequest page, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a page of elements projected to <typeparamref name="TResult"/> together
    /// with the total number of matching items.
    /// </summary>
    /// <typeparam name="TResult">The projection result type.</typeparam>
    /// <param name="page">The page to retrieve.</param>
    /// <param name="map">The projection to apply.</param>
    /// <param name="options">Optional query shaping (filter, sorting, includes, tracking).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The requested page of projected results.</returns>
    Task<PagedResult<TResult>> GetPagedAsync<TResult>(PageRequest page, Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the elements matching the supplied options.
    /// </summary>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of matching elements.</returns>
    Task<long> CountAsync(QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether any element matches the supplied options.
    /// </summary>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>true</c> when at least one element matches; otherwise <c>false</c>.</returns>
    Task<bool> AnyAsync(QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether all elements matching the supplied options satisfy the predicate.
    /// </summary>
    /// <param name="predicate">The predicate that every element must satisfy.</param>
    /// <param name="options">Optional query shaping, including a scope filter to apply first.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>true</c> when every matching element satisfies the predicate; otherwise <c>false</c>.</returns>
    Task<bool> AllAsync(Expression<Func<TEntity, bool>> predicate, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default);

    /// <summary>Computes the sum of a projected <see cref="int"/> value over the matching elements.</summary>
    /// <param name="selector">A projection function applied to each element.</param>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The sum of the projected values.</returns>
    Task<int> SumAsync(Expression<Func<TEntity, int>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default);

    /// <summary>Computes the sum of a projected nullable <see cref="int"/> value over the matching elements.</summary>
    /// <param name="selector">A projection function applied to each element.</param>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The sum of the projected values, or <c>null</c> when the sequence is empty.</returns>
    Task<int?> SumAsync(Expression<Func<TEntity, int?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default);

    /// <summary>Computes the sum of a projected <see cref="long"/> value over the matching elements.</summary>
    /// <param name="selector">A projection function applied to each element.</param>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The sum of the projected values.</returns>
    Task<long> SumAsync(Expression<Func<TEntity, long>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default);

    /// <summary>Computes the sum of a projected nullable <see cref="long"/> value over the matching elements.</summary>
    /// <param name="selector">A projection function applied to each element.</param>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The sum of the projected values, or <c>null</c> when the sequence is empty.</returns>
    Task<long?> SumAsync(Expression<Func<TEntity, long?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default);

    /// <summary>Computes the sum of a projected <see cref="double"/> value over the matching elements.</summary>
    /// <param name="selector">A projection function applied to each element.</param>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The sum of the projected values.</returns>
    Task<double> SumAsync(Expression<Func<TEntity, double>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default);

    /// <summary>Computes the sum of a projected nullable <see cref="double"/> value over the matching elements.</summary>
    /// <param name="selector">A projection function applied to each element.</param>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The sum of the projected values, or <c>null</c> when the sequence is empty.</returns>
    Task<double?> SumAsync(Expression<Func<TEntity, double?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default);

    /// <summary>Computes the sum of a projected <see cref="float"/> value over the matching elements.</summary>
    /// <param name="selector">A projection function applied to each element.</param>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The sum of the projected values.</returns>
    Task<float> SumAsync(Expression<Func<TEntity, float>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default);

    /// <summary>Computes the sum of a projected nullable <see cref="float"/> value over the matching elements.</summary>
    /// <param name="selector">A projection function applied to each element.</param>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The sum of the projected values, or <c>null</c> when the sequence is empty.</returns>
    Task<float?> SumAsync(Expression<Func<TEntity, float?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default);

    /// <summary>Computes the sum of a projected <see cref="decimal"/> value over the matching elements.</summary>
    /// <param name="selector">A projection function applied to each element.</param>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The sum of the projected values.</returns>
    Task<decimal> SumAsync(Expression<Func<TEntity, decimal>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default);

    /// <summary>Computes the sum of a projected nullable <see cref="decimal"/> value over the matching elements.</summary>
    /// <param name="selector">A projection function applied to each element.</param>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The sum of the projected values, or <c>null</c> when the sequence is empty.</returns>
    Task<decimal?> SumAsync(Expression<Func<TEntity, decimal?>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default);
}
