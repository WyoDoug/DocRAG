// PackageFilter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Models;

#endregion

namespace DocRAG.Ingestion.Scanning;

/// <summary>
///     Filters out framework, tooling, and test packages that don't need
///     documentation scraping. Per-ecosystem skip lists.
/// </summary>
public class PackageFilter
{
    /// <summary>
    ///     Filter a list of dependencies, removing framework and tooling packages.
    /// </summary>
    public IReadOnlyList<PackageDependency> Filter(IReadOnlyList<PackageDependency> dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);

        var result = dependencies
                     .Where(d => !ShouldSkip(d))
                     .ToList();
        return result;
    }

    private static bool ShouldSkip(PackageDependency dependency)
    {
        var result = false;
        if (smSkipPrefixes.TryGetValue(dependency.EcosystemId, out var prefixes))
        {
            result = prefixes.Any(prefix =>
                                      dependency.PackageId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                 );
        }

        return result;
    }

    private static readonly Dictionary<string, IReadOnlyList<string>> smSkipPrefixes =
        new Dictionary<string, IReadOnlyList<string>>
            {
                ["nuget"] =
                    [
                        "Microsoft.Extensions.", "Microsoft.AspNetCore.",
                        "Microsoft.EntityFrameworkCore.", "System.",
                        "Microsoft.NET.", "Microsoft.NETCore.", "NETStandard.",
                        "xunit", "NUnit", "Moq", "FluentAssertions",
                        "coverlet.", "Microsoft.TestPlatform", "MSTest.",
                        "CodeStructure.Analyzers"
                    ],
                ["npm"] =
                    [
                        "@types/", "eslint", "prettier", "typescript",
                        "webpack", "jest", "mocha", "babel",
                        "postcss", "autoprefixer", "vite", "rollup"
                    ],
                ["pip"] =
                    [
                        "setuptools", "pip", "wheel", "pytest",
                        "black", "flake8", "mypy", "pylint",
                        "isort", "tox", "coverage", "twine"
                    ]
            };
}
