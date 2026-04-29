// PackageDependency.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace SaddleRAG.Core.Models;

/// <summary>
///     A single package dependency discovered in a project file.
/// </summary>
public record PackageDependency
{
    /// <summary>
    ///     The package identifier (e.g., "Newtonsoft.Json").
    /// </summary>
    public required string PackageId { get; init; }

    /// <summary>
    ///     The resolved or declared version string.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    ///     The package ecosystem (e.g., "nuget", "npm", "pypi").
    /// </summary>
    public required string EcosystemId { get; init; }
}
