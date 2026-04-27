// VersionDiffRecord.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace DocRAG.Core.Models;

/// <summary>
///     Diff between two versions of the same library, generated during version upgrade.
/// </summary>
public record VersionDiffRecord
{
    /// <summary>
    ///     Unique identifier. Example: "infragistics-wpf/25.1-to-25.2"
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     Parent library identifier.
    /// </summary>
    public required string LibraryId { get; init; }

    /// <summary>
    ///     The older version being compared from.
    /// </summary>
    public required string FromVersion { get; init; }

    /// <summary>
    ///     The newer version being compared to.
    /// </summary>
    public required string ToVersion { get; init; }

    /// <summary>
    ///     When this diff was generated.
    /// </summary>
    public required DateTime GeneratedAt { get; init; }

    /// <summary>
    ///     Pages present in new version but not in old.
    /// </summary>
    public required IReadOnlyList<PageDiffEntry> AddedPages { get; init; }

    /// <summary>
    ///     Pages present in old version but not in new.
    /// </summary>
    public required IReadOnlyList<PageDiffEntry> RemovedPages { get; init; }

    /// <summary>
    ///     Pages present in both but with different ContentHash.
    /// </summary>
    public required IReadOnlyList<PageChangeDiffEntry> ChangedPages { get; init; }

    /// <summary>
    ///     Count of pages with identical ContentHash across versions.
    /// </summary>
    public required int UnchangedPageCount { get; init; }
}
