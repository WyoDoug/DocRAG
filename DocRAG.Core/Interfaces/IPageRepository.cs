// IPageRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Models;

#endregion

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace DocRAG.Core.Interfaces;

/// <summary>
///     Data access for scraped page records.
/// </summary>
public interface IPageRepository
{
    /// <summary>
    ///     Store a scraped page.
    /// </summary>
    Task UpsertPageAsync(PageRecord page, CancellationToken ct = default);

    /// <summary>
    ///     Get all pages for a library version.
    /// </summary>
    Task<IReadOnlyList<PageRecord>> GetPagesAsync(string libraryId, string version, CancellationToken ct = default);

    /// <summary>
    ///     Get a page by its URL within a library version.
    /// </summary>
    Task<PageRecord?> GetPageByUrlAsync(string libraryId, string version, string url, CancellationToken ct = default);

    /// <summary>
    ///     Get page count for a library version.
    /// </summary>
    Task<int> GetPageCountAsync(string libraryId, string version, CancellationToken ct = default);

    /// <summary>
    ///     Delete all pages for a library version.
    /// </summary>
    Task<long> DeleteAsync(string libraryId, string version, CancellationToken ct = default);
}
