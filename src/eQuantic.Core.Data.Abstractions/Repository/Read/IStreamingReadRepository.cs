using System.Collections.Generic;
using System.Threading;
using eQuantic.Core.Data.Repository.Options;

namespace eQuantic.Core.Data.Repository.Read;

/// <summary>
///     A read repository that streams results through the store's native cursor/paging path as an
///     <see cref="IAsyncEnumerable{T}" /> — rows arrive as the store produces them, nothing is buffered beyond a
///     page, and enumeration can stop (or be cancelled) at any point without fetching the rest. The natural shape
///     for exports, ETL and any read too large to materialize. Providers opt in.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public interface IStreamingReadRepository<TEntity>
    where TEntity : class
{
    /// <summary>Streams the elements matching the supplied options.</summary>
    /// <param name="options">Optional query shaping (filter, sorting).</param>
    /// <param name="cancellationToken">The cancellation token (also observed between pages).</param>
    /// <returns>The matching entities, streamed page by page.</returns>
    IAsyncEnumerable<TEntity> GetStreamAsync(QueryOptions<TEntity>? options = null, CancellationToken cancellationToken = default);
}
