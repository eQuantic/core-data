using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Linq.Specification;

namespace eQuantic.Core.Data.Repository.Read;

/// <summary>
/// The synchronous read repository. It mirrors <see cref="IAsyncReadRepository{TEntity, TKey}"/>
/// and shapes queries through a single <see cref="QueryOptions{TEntity}"/> argument.
/// </summary>
/// <typeparam name="TEntity">The type of the entity.</typeparam>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <seealso cref="IRepository" />
public interface IReadRepository<TEntity, TKey> : IRepository
    where TEntity : class, IEntity<TKey>
{
    /// <summary>
    /// Gets the element identified by its key.
    /// </summary>
    /// <param name="id">The entity key.</param>
    /// <param name="options">Optional query shaping.</param>
    /// <returns>The entity, or <c>null</c> when not found.</returns>
    TEntity? Get(TKey id, QueryOptions<TEntity>? options = null);

    /// <summary>
    /// Gets all elements, optionally shaped by <paramref name="options"/>.
    /// </summary>
    /// <param name="options">Optional query shaping (filter, sorting, includes, tracking).</param>
    /// <returns>The matching entities.</returns>
    IEnumerable<TEntity> GetAll(QueryOptions<TEntity>? options = null);

    /// <summary>
    /// Gets all elements matching the supplied predicate.
    /// </summary>
    /// <param name="filter">The predicate to apply.</param>
    /// <param name="options">Optional additional query shaping.</param>
    /// <returns>The matching entities.</returns>
    IEnumerable<TEntity> GetFiltered(Expression<Func<TEntity, bool>> filter, QueryOptions<TEntity>? options = null);

    /// <summary>
    /// Gets all elements matching the supplied specification.
    /// </summary>
    /// <param name="specification">The specification to apply.</param>
    /// <param name="options">Optional additional query shaping.</param>
    /// <returns>The matching entities.</returns>
    IEnumerable<TEntity> AllMatching(ISpecification<TEntity> specification, QueryOptions<TEntity>? options = null);

    /// <summary>
    /// Gets all elements projected to <typeparamref name="TResult"/>.
    /// </summary>
    /// <typeparam name="TResult">The projection result type.</typeparam>
    /// <param name="map">The projection to apply.</param>
    /// <param name="options">Optional query shaping (filter, sorting, includes, tracking).</param>
    /// <returns>The projected results.</returns>
    IEnumerable<TResult> GetMapped<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null);

    /// <summary>
    /// Gets the first element matching the supplied options, or <c>null</c> when none match.
    /// </summary>
    /// <param name="options">The query shaping, including the filter to apply.</param>
    /// <returns>The first matching entity, or <c>null</c>.</returns>
    TEntity? GetFirst(QueryOptions<TEntity> options);

    /// <summary>
    /// Gets the first element matching the supplied options, projected to <typeparamref name="TResult"/>.
    /// </summary>
    /// <typeparam name="TResult">The projection result type.</typeparam>
    /// <param name="map">The projection to apply.</param>
    /// <param name="options">The query shaping, including the filter to apply.</param>
    /// <returns>The first projected result, or the default when none match.</returns>
    TResult? GetFirstMapped<TResult>(Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity> options);

    /// <summary>
    /// Gets the single element matching the supplied options, or <c>null</c> when none match.
    /// </summary>
    /// <param name="options">The query shaping, including the filter to apply.</param>
    /// <returns>The single matching entity, or <c>null</c>.</returns>
    TEntity? GetSingle(QueryOptions<TEntity> options);

    /// <summary>
    /// Gets a page of elements together with the total number of matching items.
    /// </summary>
    /// <param name="page">The page to retrieve.</param>
    /// <param name="options">Optional query shaping (filter, sorting, includes, tracking).</param>
    /// <returns>The requested page of entities.</returns>
    PagedResult<TEntity> GetPaged(PageRequest page, QueryOptions<TEntity>? options = null);

    /// <summary>
    /// Gets a page of elements projected to <typeparamref name="TResult"/> together
    /// with the total number of matching items.
    /// </summary>
    /// <typeparam name="TResult">The projection result type.</typeparam>
    /// <param name="page">The page to retrieve.</param>
    /// <param name="map">The projection to apply.</param>
    /// <param name="options">Optional query shaping (filter, sorting, includes, tracking).</param>
    /// <returns>The requested page of projected results.</returns>
    PagedResult<TResult> GetPaged<TResult>(PageRequest page, Expression<Func<TEntity, TResult>> map, QueryOptions<TEntity>? options = null);

    /// <summary>
    /// Counts the elements matching the supplied options.
    /// </summary>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <returns>The number of matching elements.</returns>
    long Count(QueryOptions<TEntity>? options = null);

    /// <summary>
    /// Determines whether any element matches the supplied options.
    /// </summary>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <returns><c>true</c> when at least one element matches; otherwise <c>false</c>.</returns>
    bool Any(QueryOptions<TEntity>? options = null);

    /// <summary>
    /// Determines whether all elements matching the supplied options satisfy the predicate.
    /// </summary>
    /// <param name="predicate">The predicate that every element must satisfy.</param>
    /// <param name="options">Optional query shaping, including a scope filter to apply first.</param>
    /// <returns><c>true</c> when every matching element satisfies the predicate; otherwise <c>false</c>.</returns>
    bool All(Expression<Func<TEntity, bool>> predicate, QueryOptions<TEntity>? options = null);

    /// <summary>Computes the sum of a projected <see cref="int"/> value over the matching elements.</summary>
    /// <param name="selector">A projection function applied to each element.</param>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <returns>The sum of the projected values.</returns>
    int Sum(Expression<Func<TEntity, int>> selector, QueryOptions<TEntity>? options = null);

    /// <summary>Computes the sum of a projected nullable <see cref="int"/> value over the matching elements.</summary>
    /// <param name="selector">A projection function applied to each element.</param>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <returns>The sum of the projected values, or <c>null</c> when the sequence is empty.</returns>
    int? Sum(Expression<Func<TEntity, int?>> selector, QueryOptions<TEntity>? options = null);

    /// <summary>Computes the sum of a projected <see cref="long"/> value over the matching elements.</summary>
    /// <param name="selector">A projection function applied to each element.</param>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <returns>The sum of the projected values.</returns>
    long Sum(Expression<Func<TEntity, long>> selector, QueryOptions<TEntity>? options = null);

    /// <summary>Computes the sum of a projected nullable <see cref="long"/> value over the matching elements.</summary>
    /// <param name="selector">A projection function applied to each element.</param>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <returns>The sum of the projected values, or <c>null</c> when the sequence is empty.</returns>
    long? Sum(Expression<Func<TEntity, long?>> selector, QueryOptions<TEntity>? options = null);

    /// <summary>Computes the sum of a projected <see cref="double"/> value over the matching elements.</summary>
    /// <param name="selector">A projection function applied to each element.</param>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <returns>The sum of the projected values.</returns>
    double Sum(Expression<Func<TEntity, double>> selector, QueryOptions<TEntity>? options = null);

    /// <summary>Computes the sum of a projected nullable <see cref="double"/> value over the matching elements.</summary>
    /// <param name="selector">A projection function applied to each element.</param>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <returns>The sum of the projected values, or <c>null</c> when the sequence is empty.</returns>
    double? Sum(Expression<Func<TEntity, double?>> selector, QueryOptions<TEntity>? options = null);

    /// <summary>Computes the sum of a projected <see cref="float"/> value over the matching elements.</summary>
    /// <param name="selector">A projection function applied to each element.</param>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <returns>The sum of the projected values.</returns>
    float Sum(Expression<Func<TEntity, float>> selector, QueryOptions<TEntity>? options = null);

    /// <summary>Computes the sum of a projected nullable <see cref="float"/> value over the matching elements.</summary>
    /// <param name="selector">A projection function applied to each element.</param>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <returns>The sum of the projected values, or <c>null</c> when the sequence is empty.</returns>
    float? Sum(Expression<Func<TEntity, float?>> selector, QueryOptions<TEntity>? options = null);

    /// <summary>Computes the sum of a projected <see cref="decimal"/> value over the matching elements.</summary>
    /// <param name="selector">A projection function applied to each element.</param>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <returns>The sum of the projected values.</returns>
    decimal Sum(Expression<Func<TEntity, decimal>> selector, QueryOptions<TEntity>? options = null);

    /// <summary>Computes the sum of a projected nullable <see cref="decimal"/> value over the matching elements.</summary>
    /// <param name="selector">A projection function applied to each element.</param>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <returns>The sum of the projected values, or <c>null</c> when the sequence is empty.</returns>
    decimal? Sum(Expression<Func<TEntity, decimal?>> selector, QueryOptions<TEntity>? options = null);
}
