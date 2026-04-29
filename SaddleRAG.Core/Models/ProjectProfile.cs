// ProjectProfile.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Scanned project dependency manifest. Tracks which libraries
///     are relevant to a .NET project.
/// </summary>
public record ProjectProfile
{
    /// <summary>
    ///     Unique identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     Absolute path to the .sln or .csproj that was scanned.
    /// </summary>
    public required string ProjectPath { get; init; }

    /// <summary>
    ///     Human-readable project name.
    /// </summary>
    public required string ProjectName { get; init; }

    /// <summary>
    ///     When this project was last scanned.
    /// </summary>
    public required DateTime ScannedAt { get; init; }

    /// <summary>
    ///     Package ID â†’ Version mapping for all dependencies.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Dependencies { get; init; }

    /// <summary>
    ///     Package IDs that have been ingested into SaddleRAG.
    /// </summary>
    public required IReadOnlyList<string> IngestedPackages { get; init; }
}
