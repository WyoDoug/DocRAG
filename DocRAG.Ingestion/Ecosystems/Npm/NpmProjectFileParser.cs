// NpmProjectFileParser.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.Text.Json;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;

#endregion

namespace DocRAG.Ingestion.Ecosystems.Npm;

/// <summary>
///     Parses package.json files and extracts npm package dependencies.
/// </summary>
public sealed class NpmProjectFileParser : IProjectFileParser
{
    /// <summary>
    ///     The package ecosystem identifier for npm.
    /// </summary>
    public string EcosystemId => NpmEcosystemId;

    /// <summary>
    ///     File patterns that identify npm project files.
    /// </summary>
    public IReadOnlyList<string> FilePatterns { get; } = [PackageJsonFileName];

    /// <summary>
    ///     Parses the specified package.json file and returns all declared package dependencies.
    /// </summary>
    public async Task<IReadOnlyList<PackageDependency>> ParseAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        string json = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(continueOnCapturedContext: false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        List<PackageDependency> deps = [];

        foreach(string key in smDependencyKeys)
        {
            if (root.TryGetProperty(key, out var section))
            {
                foreach(var prop in section.EnumerateObject())
                {
                    deps.Add(new PackageDependency
                                 {
                                     PackageId = prop.Name,
                                     Version = prop.Value.GetString() ?? string.Empty,
                                     EcosystemId = EcosystemId
                                 }
                            );
                }
            }
        }

        return deps;
    }

    private const string NpmEcosystemId = "npm";
    private const string PackageJsonFileName = "package.json";
    private const string DependenciesKey = "dependencies";
    private const string DevDependenciesKey = "devDependencies";

    private static readonly string[] smDependencyKeys = [DependenciesKey, DevDependenciesKey];
}
