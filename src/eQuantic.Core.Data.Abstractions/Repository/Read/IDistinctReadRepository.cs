using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using eQuantic.Core.Data.Repository.Options;

namespace eQuantic.Core.Data.Repository.Read;

/// <summary>
///     A read repository that returns the <b>distinct values</b> of one member, or the distinct combinations of
///     several, among the rows a filter matches — each once, deduplicated by the store where it can
///     (<c>SELECT DISTINCT</c>, a <c>$group</c>, <c>SELECT DISTINCT VALUE</c>) and never by loading the rows
///     whole. Providers opt in; cast to it (<c>(IDistinctReadRepository&lt;Comment&gt;)repo</c>).
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public interface IDistinctReadRepository<TEntity>
    where TEntity : class
{
    /// <summary>
    ///     Returns each distinct value of the selected member (<c>x =&gt; x.Repository</c>), or each distinct
    ///     combination of the selected members (<c>x =&gt; new { x.Repository, x.IssueNumber }</c>), among the rows
    ///     <paramref name="filter" /> and the options' filters match. The values come back in no particular order.
    /// </summary>
    /// <typeparam name="TResult">A member's type, or an anonymous type (or record) of several members.</typeparam>
    /// <param name="selector">The member, or the members, whose distinct values to return.</param>
    /// <param name="filter">The rows to look at, or <c>null</c> for every row.</param>
    /// <param name="options">Optional further filtering (query filters apply as on any read); sorting and paging do not apply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Each distinct value or combination, once.</returns>
    Task<IReadOnlyList<TResult>> GetDistinctAsync<TResult>(
        Expression<Func<TEntity, TResult>> selector,
        Expression<Func<TEntity, bool>>? filter = null,
        QueryOptions<TEntity>? options = null,
        CancellationToken cancellationToken = default);
}
