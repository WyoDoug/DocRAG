// DocUrlResolution.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Core.Enums;

#endregion

namespace SaddleRAG.Core.Models;

/// <summary>
///     The result of resolving a documentation URL for a package.
/// </summary>
public record DocUrlResolution
{
    /// <summary>
    ///     The resolved documentation URL, or <see langword="null" /> if none was found.
    /// </summary>
    public string? DocUrl { get; init; }

    /// <summary>
    ///     How the URL was obtained: "registry", "pattern", "github-repo", or "none".
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    ///     Confidence level that the resolved URL points to useful documentation.
    /// </summary>
    public required ScanConfidence Confidence { get; init; }
}
