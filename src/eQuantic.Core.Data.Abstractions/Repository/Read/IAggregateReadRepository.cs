using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using eQuantic.Core.Data.Repository.Options;

namespace eQuantic.Core.Data.Repository.Read;

/// <summary>
///     A read repository that computes <c>MIN</c>/<c>MAX</c>/<c>AVG</c> on the store (with a client-side fallback
///     for computed selectors), completing the aggregate surface next to the contract's <c>Count</c>/<c>Sum</c>.
///     Providers opt in.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public interface IAggregateReadRepository<TEntity>
    where TEntity : class
{
    /// <summary>The minimum of the projected value over the matching elements, or <c>default</c> when none match.</summary>
    /// <typeparam name="TResult">The projected type.</typeparam>
    /// <param name="selector">The projection.</param>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<TResult?> MinAsync<TResult>(Expression<Func<TEntity, TResult>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default);

    /// <summary>The maximum of the projected value over the matching elements, or <c>default</c> when none match.</summary>
    /// <typeparam name="TResult">The projected type.</typeparam>
    /// <param name="selector">The projection.</param>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<TResult?> MaxAsync<TResult>(Expression<Func<TEntity, TResult>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The average of the projected numeric value over the matching elements as a <see cref="double" />
    ///     (integer columns are cast before averaging, so nothing truncates), or <c>0</c> when none match.
    /// </summary>
    /// <typeparam name="TValue">The projected numeric type.</typeparam>
    /// <param name="selector">The projection.</param>
    /// <param name="options">Optional query shaping, including the filter to apply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<double> AverageAsync<TValue>(Expression<Func<TEntity, TValue>> selector, QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default);
}
