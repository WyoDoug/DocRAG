// IPackageRegistryClient.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Models;

#endregion

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace DocRAG.Core.Interfaces;

/// <summary>
///     Fetches package metadata from a package registry.
/// </summary>
public interface IPackageRegistryClient
{
    /// <summary>
    ///     The package ecosystem this client targets (e.g., "nuget", "npm", "pypi").
    /// </summary>
    string EcosystemId { get; }

    /// <summary>
    ///     Fetches metadata for the specified package and version.
    ///     Returns <see langword="null" /> when the package is not found.
    /// </summary>
    Task<PackageMetadata?> FetchMetadataAsync(string packageId, string version, CancellationToken ct = default);
}
