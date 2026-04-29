// PipProjectFileParser.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.Text.RegularExpressions;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Ecosystems.Pip;

/// <summary>
///     Parses pip project files (requirements.txt, pyproject.toml) to extract package dependencies.
/// </summary>
public sealed partial class PipProjectFileParser : IProjectFileParser
{
    /// <inheritdoc />
    public string EcosystemId => PipEcosystemId;

    /// <inheritdoc />
    public IReadOnlyList<string> FilePatterns { get; } = [RequirementsFileName, PyprojectFileName];

    /// <inheritdoc />
    public async Task<IReadOnlyList<PackageDependency>> ParseAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        string fileName = Path.GetFileName(filePath);
        string[] lines = await File.ReadAllLinesAsync(filePath, ct);

        var res = fileName.Equals(RequirementsFileName, StringComparison.OrdinalIgnoreCase)
                      ? ParseRequirementsTxt(lines)
                      : ParsePyprojectToml(lines);

        return res;
    }

    private IReadOnlyList<PackageDependency> ParseRequirementsTxt(string[] lines)
    {
        var dependencies = new List<PackageDependency>();

        foreach(string line in lines.Where(l => !string.IsNullOrWhiteSpace(l) &&
                                                !l.TrimStart().StartsWith(value: '#') &&
                                                !l.TrimStart().StartsWith(value: '-')
                                          ))
        {
            var match = RequirementLineRegex().Match(line.Trim());
            if (match.Success)
            {
                dependencies.Add(new PackageDependency
                                     {
                                         PackageId = match.Groups[groupnum: 1].Value,
                                         Version = match.Groups[groupnum: 2].Success
                                                       ? match.Groups[groupnum: 2].Value.Trim()
                                                       : string.Empty,
                                         EcosystemId = PipEcosystemId
                                     }
                                );
            }
        }

        return dependencies;
    }

    private IReadOnlyList<PackageDependency> ParsePyprojectToml(string[] lines)
    {
        var dependencies = new List<PackageDependency>();
        var inProjectSection = false;
        var inDependenciesArray = false;

        foreach(string rawLine in lines)
        {
            string line = rawLine.Trim();
            (inProjectSection, inDependenciesArray) =
                ProcessPyprojectLine(line, inProjectSection, inDependenciesArray, dependencies);
        }

        return dependencies;
    }

    private (bool InProject, bool InDeps) ProcessPyprojectLine(string line,
                                                               bool inProjectSection,
                                                               bool inDependenciesArray,
                                                               List<PackageDependency> dependencies)
    {
        var result = (inProjectSection, inDependenciesArray);

        switch(true)
        {
            case true when line.StartsWith(value: '['):
                result = (line.Equals(ProjectSectionHeader, StringComparison.OrdinalIgnoreCase), false);
                break;
            case true when inProjectSection &&
                           !inDependenciesArray &&
                           line.StartsWith(DependenciesKey, StringComparison.OrdinalIgnoreCase):
                result = (inProjectSection, true);
                break;
            case true when inDependenciesArray && line.StartsWith(value: ']'):
                result = (inProjectSection, false);
                break;
            case true when inDependenciesArray:
                TryAddPyprojectDependency(line, dependencies);
                break;
        }

        return result;
    }

    private PackageDependency? ParseDependencySpec(string spec)
    {
        var match = RequirementLineRegex().Match(spec.Trim());

        var res = match.Success
                      ? new PackageDependency
                            {
                                PackageId = match.Groups[groupnum: 1].Value,
                                Version = match.Groups[groupnum: 2].Success
                                              ? match.Groups[groupnum: 2].Value.Trim()
                                              : string.Empty,
                                EcosystemId = PipEcosystemId
                            }
                      : null;

        return res;
    }

    private void TryAddPyprojectDependency(string line, List<PackageDependency> dependencies)
    {
        var match = PyprojectDependencyRegex().Match(line);
        if (match.Success)
        {
            var parsed = ParseDependencySpec(match.Groups[groupnum: 1].Value);
            if (parsed != null)
                dependencies.Add(parsed);
        }
    }

    [GeneratedRegex(@"^([a-zA-Z0-9_.-]+)\s*(?:[=!<>~]+\s*(.+))?$")]
    private static partial Regex RequirementLineRegex();

    [GeneratedRegex(@"""([^""]+)""")]
    private static partial Regex PyprojectDependencyRegex();

    private const string PipEcosystemId = "pip";
    private const string RequirementsFileName = "requirements.txt";
    private const string PyprojectFileName = "pyproject.toml";
    private const string ProjectSectionHeader = "[project]";
    private const string DependenciesKey = "dependencies";
}
