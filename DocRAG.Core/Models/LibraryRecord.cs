// LibraryRecord.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace DocRAG.Core.Models;

/// <summary>
///     Top-level record for a documentation library. One per library, not per version.
/// </summary>
public class LibraryRecord
{
    /// <summary>
    ///     Unique identifier. Example: "infragistics-wpf"
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     Human-readable library name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     Description hint used during ingestion.
    /// </summary>
    public required string Hint { get; init; }

    /// <summary>
    ///     Latest ingested version string.
    /// </summary>
    public required string CurrentVersion { get; set; }

    /// <summary>
    ///     All versions that have been ingested.
    /// </summary>
    public required List<string> AllVersions { get; init; }
}
