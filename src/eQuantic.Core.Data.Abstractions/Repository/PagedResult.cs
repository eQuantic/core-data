using System;
using System.Collections.Generic;

namespace eQuantic.Core.Data.Repository;

/// <summary>
/// Represents a single page of results together with the total number of items
/// available across all pages.
/// </summary>
/// <typeparam name="T">The type of the items in the page.</typeparam>
public sealed class PagedResult<T>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PagedResult{T}"/> class.
    /// </summary>
    /// <param name="items">The items contained in the current page.</param>
    /// <param name="totalCount">The total number of items available across all pages.</param>
    /// <param name="pageIndex">The one-based index of the current page.</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="items"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="totalCount"/> is negative, or when
    /// <paramref name="pageIndex"/> or <paramref name="pageSize"/> is less than 1.
    /// </exception>
    public PagedResult(IReadOnlyList<T> items, long totalCount, int pageIndex, int pageSize)
    {
        if (totalCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalCount), totalCount, "The total count cannot be negative.");
        }

        if (pageIndex < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex, "The page index must be greater than or equal to 1.");
        }

        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "The page size must be greater than or equal to 1.");
        }

        Items = items ?? throw new ArgumentNullException(nameof(items));
        TotalCount = totalCount;
        PageIndex = pageIndex;
        PageSize = pageSize;
    }

    /// <summary>
    /// Gets the items contained in the current page.
    /// </summary>
    public IReadOnlyList<T> Items { get; }

    /// <summary>
    /// Gets the total number of items available across all pages.
    /// </summary>
    public long TotalCount { get; }

    /// <summary>
    /// Gets the one-based index of the current page.
    /// </summary>
    public int PageIndex { get; }

    /// <summary>
    /// Gets the number of items per page.
    /// </summary>
    public int PageSize { get; }

    /// <summary>
    /// Gets the total number of pages, computed from <see cref="TotalCount"/> and <see cref="PageSize"/>.
    /// </summary>
    public int PageCount => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    /// <summary>
    /// Gets a value indicating whether a page exists before the current one.
    /// </summary>
    public bool HasPreviousPage => PageIndex > 1;

    /// <summary>
    /// Gets a value indicating whether a page exists after the current one.
    /// </summary>
    public bool HasNextPage => PageIndex < PageCount;

    /// <summary>
    /// Creates an empty <see cref="PagedResult{T}"/> for the given page request.
    /// </summary>
    /// <param name="pageIndex">The one-based index of the current page.</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <returns>An empty <see cref="PagedResult{T}"/> with a total count of zero.</returns>
    public static PagedResult<T> Empty(int pageIndex = 1, int pageSize = PageRequest.DefaultPageSize) =>
        new(Array.Empty<T>(), 0, pageIndex, pageSize);
}
