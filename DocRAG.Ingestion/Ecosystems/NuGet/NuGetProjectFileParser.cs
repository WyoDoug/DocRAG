// NuGetProjectFileParser.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;

#endregion

namespace DocRAG.Ingestion.Ecosystems.NuGet;

/// <summary>
///     Parses .sln, .slnx, and .csproj files to extract NuGet package dependencies.
/// </summary>
public class NuGetProjectFileParser : IProjectFileParser
{
    /// <inheritdoc />
    public string EcosystemId => EcosystemIdentifier;

    /// <inheritdoc />
    public IReadOnlyList<string> FilePatterns => ["**/*.sln", "**/*.slnx", "**/*.csproj"];

    /// <inheritdoc />
    public async Task<IReadOnlyList<PackageDependency>> ParseAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        string extension = Path.GetExtension(filePath).ToLowerInvariant();

        var res = extension switch
            {
                ".sln" => await ParseSlnAsync(filePath, ct).ConfigureAwait(continueOnCapturedContext: false),
                ".slnx" => await ParseSlnxAsync(filePath, ct).ConfigureAwait(continueOnCapturedContext: false),
                ".csproj" => await ParseCsprojAsync(filePath, ct).ConfigureAwait(continueOnCapturedContext: false),
                var _ => []
            };

        return res;
    }

    private async Task<IReadOnlyList<PackageDependency>> ParseSlnAsync(string slnPath, CancellationToken ct)
    {
        string content = await File.ReadAllTextAsync(slnPath, ct).ConfigureAwait(continueOnCapturedContext: false);
        string baseDir = Path.GetDirectoryName(slnPath) ?? string.Empty;

        var csprojPaths = smSlnProjectPattern.Matches(content)
                                             .Select(m => Path.GetFullPath(Path.Combine(baseDir,
                                                                                    m.Groups[groupnum: 1]
                                                                                        .Value.Replace(oldChar: '\\',
                                                                                                 Path
                                                                                                     .DirectorySeparatorChar
                                                                                            )
                                                                               )
                                                                          )
                                                    )
                                             .Where(File.Exists)
                                             .ToList();

        List<PackageDependency> res = [];

        foreach(string csprojPath in csprojPaths)
        {
            var deps = await ParseCsprojAsync(csprojPath, ct).ConfigureAwait(continueOnCapturedContext: false);
            res.AddRange(deps);
        }

        return res;
    }

    private async Task<IReadOnlyList<PackageDependency>> ParseSlnxAsync(string slnxPath, CancellationToken ct)
    {
        string content = await File.ReadAllTextAsync(slnxPath, ct).ConfigureAwait(continueOnCapturedContext: false);
        string baseDir = Path.GetDirectoryName(slnxPath) ?? string.Empty;
        var doc = XDocument.Parse(content);

        var csprojPaths = doc.Descendants()
                             .Where(e => string.Equals(e.Name.LocalName, ProjectElement, StringComparison.OrdinalIgnoreCase))
                             .Select(e => e.Attribute(PathAttribute)?.Value ?? string.Empty)
                             .Where(p => p.EndsWith(CsprojExtension, StringComparison.OrdinalIgnoreCase))
                             .Select(p => Path.GetFullPath(Path.Combine(baseDir,
                                                                        p.Replace(oldChar: '\\',
                                                                                 Path.DirectorySeparatorChar
                                                                            )
                                                                       )
                                                          )
                                    )
                             .Where(File.Exists)
                             .ToList();

        List<PackageDependency> res = [];

        foreach(string csprojPath in csprojPaths)
        {
            var deps = await ParseCsprojAsync(csprojPath, ct).ConfigureAwait(continueOnCapturedContext: false);
            res.AddRange(deps);
        }

        return res;
    }

    private static async Task<IReadOnlyList<PackageDependency>> ParseCsprojAsync(
        string csprojPath,
        CancellationToken ct)
    {
        string content = await File.ReadAllTextAsync(csprojPath, ct).ConfigureAwait(continueOnCapturedContext: false);
        var doc = XDocument.Parse(content);

        var res = doc.Descendants(PackageReferenceElement)
                     .Select(e => new
                                      {
                                          Id = e.Attribute(IncludeAttribute)?.Value ?? string.Empty,
                                          Ver = e.Attribute(VersionAttribute)?.Value ??
                                                e.Element(VersionAttribute)?.Value ?? UnknownVersion
                                      }
                            )
                     .Where(x => !string.IsNullOrWhiteSpace(x.Id))
                     .Select(x => new PackageDependency
                                      {
                                          PackageId = x.Id,
                                          Version = x.Ver,
                                          EcosystemId = EcosystemIdentifier
                                      }
                            )
                     .ToList();

        return res;
    }

    private const string EcosystemIdentifier = "nuget";
    private const string PackageReferenceElement = "PackageReference";
    private const string IncludeAttribute = "Include";
    private const string VersionAttribute = "Version";
    private const string UnknownVersion = "unknown";

    private static readonly Regex smSlnProjectPattern =
        new Regex(@"Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*"",\s*""([^""]*\.csproj)""",
                  RegexOptions.IgnoreCase | RegexOptions.Compiled
                 );

    private const string ProjectElement = "Project";
    private const string PathAttribute = "Path";
    private const string CsprojExtension = ".csproj";
}
