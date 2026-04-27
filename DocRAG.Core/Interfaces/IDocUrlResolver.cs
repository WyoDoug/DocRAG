// IDocUrlResolver.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Models;

#endregion

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace DocRAG.Core.Interfaces;

/// <summary>
///     Resolves a documentation URL from package metadata.
/// </summary>
public interface IDocUrlResolver
{
    /// <summary>
    ///     The package ecosystem this resolver handles (e.g., "nuget", "npm", "pypi").
    /// </summary>
    string EcosystemId { get; }

    /// <summary>
    ///     Resolves the best available documentation URL for the given package metadata.
    /// </summary>
    Task<DocUrlResolution> ResolveAsync(PackageMetadata metadata, CancellationToken ct = default);
}
