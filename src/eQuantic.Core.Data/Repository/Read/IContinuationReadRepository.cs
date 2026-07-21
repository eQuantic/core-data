using System.Threading;
using System.Threading.Tasks;
using eQuantic.Core.Data.Repository.Options;

namespace eQuantic.Core.Data.Repository.Read;

/// <summary>
///     A read repository that pages through its store's native continuation mechanism (Cassandra
///     <c>PagingState</c>, Cosmos continuation tokens) instead of count-and-skip: every page costs the same,
///     however deep, and the token is an opaque string a caller can hold across requests. Providers opt in.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public interface IContinuationReadRepository<TEntity>
    where TEntity : class
{
    /// <summary>
    ///     Reads one page, resuming after <paramref name="continuationToken" /> when supplied. The page size is
    ///     the store's fetch hint — a page may hold fewer items (and client-evaluated residual filters reduce it
    ///     further); the read is exhausted when the returned token is <c>null</c>.
    /// </summary>
    /// <param name="pageSize">The page size hint (at least 1).</param>
    /// <param name="continuationToken">The token from the previous page, or <c>null</c> for the first page.</param>
    /// <param name="options">Optional query shaping (filter, sorting).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The page and the token resuming after it.</returns>
    Task<ContinuedResult<TEntity>> GetPageAsync(int pageSize, string? continuationToken = null,
        QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default);
}
