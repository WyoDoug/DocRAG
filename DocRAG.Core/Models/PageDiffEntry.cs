// PageDiffEntry.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;

#endregion

namespace DocRAG.Core.Models;

/// <summary>
///     A page that was added or removed between versions.
/// </summary>
public record PageDiffEntry
{
    /// <summary>
    ///     Page URL.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    ///     Page title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    ///     Page classification category.
    /// </summary>
    public required DocCategory Category { get; init; }
}
