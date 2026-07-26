using System;
using System.Collections.Generic;

namespace eQuantic.Core.Data.Repository;

/// <summary>
///     A single page of a token-continued read: the items plus the opaque token that resumes after them. Unlike
///     <see cref="PagedResult{T}" /> there is no total count — that is the point: the store walks its native
///     paging path (Cassandra <c>PagingState</c>, Cosmos continuation) instead of counting and skipping, so deep
///     pages cost the same as the first one.
/// </summary>
/// <typeparam name="T">The type of the items in the page.</typeparam>
public sealed class ContinuedResult<T>
{
    /// <summary>Initializes the page.</summary>
    /// <param name="items">The items in this page.</param>
    /// <param name="continuationToken">The opaque token resuming after this page, or <c>null</c> when exhausted.</param>
    public ContinuedResult(IReadOnlyList<T> items, string? continuationToken)
    {
        Items = items ?? throw new ArgumentNullException(nameof(items));
        ContinuationToken = continuationToken;
    }

    /// <summary>The items in this page.</summary>
    public IReadOnlyList<T> Items { get; }

    /// <summary>
    ///     The opaque, serializable token that resumes the read after this page, or <c>null</c> when the read is
    ///     exhausted. Treat it as a black box: pass it back unchanged.
    /// </summary>
    public string? ContinuationToken { get; }
}
