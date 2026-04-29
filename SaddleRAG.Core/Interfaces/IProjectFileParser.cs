// IProjectFileParser.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Core.Models;

#endregion

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     Parses a project file and extracts its package dependencies.
/// </summary>
public interface IProjectFileParser
{
    /// <summary>
    ///     The package ecosystem this parser handles (e.g., "nuget", "npm", "pypi").
    /// </summary>
    string EcosystemId { get; }

    /// <summary>
    ///     Glob patterns that identify project files this parser can handle
    ///     (e.g., "**/*.csproj", "**/packages.json").
    /// </summary>
    IReadOnlyList<string> FilePatterns { get; }

    /// <summary>
    ///     Parses the specified project file and returns all declared package dependencies.
    /// </summary>
    Task<IReadOnlyList<PackageDependency>> ParseAsync(string filePath, CancellationToken ct = default);
}
