using System;

namespace eQuantic.Core.Data.Repository;

/// <summary>
/// Represents a request for a single page of data, expressed as a one-based
/// page index and a page size.
/// </summary>
public sealed class PageRequest
{
    /// <summary>
    /// The default number of items per page when none is specified.
    /// </summary>
    public const int DefaultPageSize = 20;

    /// <summary>
    /// Initializes a new instance of the <see cref="PageRequest"/> class.
    /// </summary>
    /// <param name="pageIndex">The one-based index of the page to retrieve. Must be greater than or equal to 1.</param>
    /// <param name="pageSize">The number of items per page. Must be greater than or equal to 1.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="pageIndex"/> or <paramref name="pageSize"/> is less than 1.
    /// </exception>
    public PageRequest(int pageIndex = 1, int pageSize = DefaultPageSize)
    {
        if (pageIndex < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex, "The page index must be greater than or equal to 1.");
        }

        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "The page size must be greater than or equal to 1.");
        }

        PageIndex = pageIndex;
        PageSize = pageSize;
    }

    /// <summary>
    /// Gets the one-based index of the page to retrieve. The first page is <c>1</c>.
    /// </summary>
    public int PageIndex { get; }

    /// <summary>
    /// Gets the number of items per page.
    /// </summary>
    public int PageSize { get; }

    /// <summary>
    /// Gets the number of items to skip before the current page, computed from
    /// <see cref="PageIndex"/> and <see cref="PageSize"/>.
    /// </summary>
    public int Skip => (PageIndex - 1) * PageSize;

    /// <summary>
    /// Gets the number of items to take for the current page. Equivalent to <see cref="PageSize"/>.
    /// </summary>
    public int Take => PageSize;

    /// <summary>
    /// Creates a <see cref="PageRequest"/> from a one-based page index and page size.
    /// </summary>
    /// <param name="pageIndex">The one-based index of the page to retrieve.</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <returns>A new <see cref="PageRequest"/>.</returns>
    public static PageRequest Of(int pageIndex, int pageSize) => new(pageIndex, pageSize);
}
