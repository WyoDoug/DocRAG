// PageRecord.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Core.Enums;

#endregion

namespace SaddleRAG.Core.Models;

/// <summary>
///     A scraped documentation page with its raw content and classification.
/// </summary>
public record PageRecord
{
    /// <summary>
    ///     Unique identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     Parent library identifier.
    /// </summary>
    public required string LibraryId { get; init; }

    /// <summary>
    ///     Version of the library this page belongs to.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    ///     Original URL where this page was fetched from.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    ///     Page title extracted from the HTML.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    ///     Assigned documentation category.
    /// </summary>
    public required DocCategory Category { get; init; }

    /// <summary>
    ///     Extracted main content with navigation/chrome stripped.
    /// </summary>
    public required string RawContent { get; init; }

    /// <summary>
    ///     When this page was fetched.
    /// </summary>
    public required DateTime FetchedAt { get; init; }

    /// <summary>
    ///     SHA-256 hash of RawContent for change detection across versions.
    /// </summary>
    public required string ContentHash { get; init; }
}
