// PackageIndexStatus.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace SaddleRAG.Core.Models;

/// <summary>
///     The indexing status of a single package processed during dependency scanning.
/// </summary>
public record PackageIndexStatus
{
    /// <summary>
    ///     The package identifier.
    /// </summary>
    public required string PackageId { get; init; }

    /// <summary>
    ///     The package version string.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    ///     The package ecosystem (e.g., "nuget", "npm", "pypi").
    /// </summary>
    public required string EcosystemId { get; init; }

    /// <summary>
    ///     Disposition of this package: "queued", "cached", "cached-different-version",
    ///     "filtered", or "failed".
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    ///     The documentation URL that will be or was scraped, if resolved.
    /// </summary>
    public string? DocUrl { get; init; }

    /// <summary>
    ///     The version already present in cache when Status is "cached-different-version".
    /// </summary>
    public string? CachedVersion { get; init; }

    /// <summary>
    ///     Error detail when Status is "failed".
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    ///     The scrape job identifier created for this package, if applicable.
    /// </summary>
    public string? JobId { get; init; }
}
