// DependencyIndexReport.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Summary report produced after scanning a project file for dependencies to index.
/// </summary>
public record DependencyIndexReport
{
    /// <summary>
    ///     Absolute path to the project file that was scanned.
    /// </summary>
    public required string ProjectPath { get; init; }

    /// <summary>
    ///     Total number of dependencies found in the project file.
    /// </summary>
    public required int TotalDependencies { get; init; }

    /// <summary>
    ///     Number of dependencies excluded by ecosystem or version filters.
    /// </summary>
    public required int FilteredOut { get; init; }

    /// <summary>
    ///     Number of dependencies whose exact version is already cached.
    /// </summary>
    public required int AlreadyCached { get; init; }

    /// <summary>
    ///     Number of dependencies cached under a different version.
    /// </summary>
    public required int CachedDifferentVersion { get; init; }

    /// <summary>
    ///     Number of dependencies newly enqueued for scraping.
    /// </summary>
    public required int NewlyQueued { get; init; }

    /// <summary>
    ///     Number of dependencies for which URL resolution or job creation failed.
    /// </summary>
    public required int ResolutionFailed { get; init; }

    /// <summary>
    ///     Per-package status detail for every dependency that was processed.
    /// </summary>
    public required IReadOnlyList<PackageIndexStatus> Packages { get; init; }
}
