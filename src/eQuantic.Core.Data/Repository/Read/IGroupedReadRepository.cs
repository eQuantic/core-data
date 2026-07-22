using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using eQuantic.Core.Data.Repository.Options;

namespace eQuantic.Core.Data.Repository.Read;

/// <summary>
///     A read repository that groups <b>on the store</b>: the key and the aggregate projection render to a
///     native <c>GROUP BY</c>, and only the grouped rows travel. The projection is typed LINQ restricted to the
///     shapes a store can aggregate server-side — <c>g.Key</c>, <c>g.Count()</c>,
///     <c>g.Sum/Min/Max/Average(x =&gt; x.Member)</c>; anything else is rejected with the supported shapes.
///     Providers opt in.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public interface IGroupedReadRepository<TEntity>
    where TEntity : class
{
    /// <summary>
    ///     Groups the matching elements by <paramref name="keySelector" /> and projects each group with
    ///     <paramref name="resultSelector" /> — computed on the store.
    /// </summary>
    /// <typeparam name="TKey">The key type (a member, or an anonymous composite of members).</typeparam>
    /// <typeparam name="TResult">The projected result type (anonymous or member-init).</typeparam>
    /// <param name="keySelector">The grouping key selector.</param>
    /// <param name="resultSelector">The per-group projection (<c>g =&gt; new { g.Key, Total = g.Sum(x =&gt; x.Total) }</c>).</param>
    /// <param name="options">Optional query shaping — the filter applies <b>before</b> grouping; sorting does not apply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>One projected result per group.</returns>
    Task<IReadOnlyList<TResult>> GroupByAsync<TKey, TResult>(
        Expression<Func<TEntity, TKey>> keySelector,
        Expression<Func<IGrouping<TKey, TEntity>, TResult>> resultSelector,
        QueryOptions<TEntity>? options = null,
        CancellationToken cancellationToken = default);
}
