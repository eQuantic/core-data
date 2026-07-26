using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using eQuantic.Core.Data.Query;

namespace eQuantic.Core.Data.Repository;

/// <summary>
///     A unit of work that runs a typed <c>UNION</c>/<c>UNION ALL</c> <b>on the store</b>: every branch's filter
///     and projection push down, the store combines the branches (deduplicating for
///     <see cref="UnionQuery.Distinct{TResult}" />), and only the combined rows travel. Providers opt in.
/// </summary>
public interface IUnionQueryRunner
{
    /// <summary>Runs the union and materializes the combined rows into the common shape.</summary>
    /// <typeparam name="TResult">The common result shape.</typeparam>
    /// <param name="query">The composed union.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The combined rows, in the query's order.</returns>
    Task<IReadOnlyList<TResult>> UnionAsync<TResult>(UnionQuery<TResult> query, CancellationToken cancellationToken = default);
}
