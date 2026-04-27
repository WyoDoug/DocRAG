// PipDocUrlResolver.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using DocRAG.Ingestion.Ecosystems.Common;

#endregion

namespace DocRAG.Ingestion.Ecosystems.Pip;

/// <summary>
///     Resolves a documentation URL for pip packages using a cascade strategy.
/// </summary>
public sealed class PipDocUrlResolver : IDocUrlResolver
{
    /// <summary>
    ///     Initializes a new instance of <see cref="PipDocUrlResolver" />.
    /// </summary>
    public PipDocUrlResolver(CommonDocUrlPatterns commonPatterns)
    {
        mCommonPatterns = commonPatterns;
    }

    private readonly CommonDocUrlPatterns mCommonPatterns;

    /// <inheritdoc />
    public string EcosystemId => PipEcosystemId;

    /// <inheritdoc />
    public async Task<DocUrlResolution> ResolveAsync(PackageMetadata metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var res = await ResolveByStrategyAsync(metadata, ct);
        return res;
    }

    private async Task<DocUrlResolution> ResolveByStrategyAsync(PackageMetadata metadata, CancellationToken ct)
    {
        bool hasDocUrl = !string.IsNullOrEmpty(metadata.DocumentationUrl);
        bool hasNonGitHubProject = !string.IsNullOrEmpty(metadata.ProjectUrl) && !IsGitHub(metadata.ProjectUrl);
        bool hasGitHub = TryGetGitHubUrl(metadata, out string gitHubUrl);

        var res = (hasDocUrl, hasNonGitHubProject, hasGitHub) switch
            {
                (true, var _, var _) => new DocUrlResolution
                                            {
                                                DocUrl = metadata.DocumentationUrl,
                                                Source = SourceRegistry,
                                                Confidence = ScanConfidence.High
                                            },
                (var _, true, var _) => new DocUrlResolution
                                            {
                                                DocUrl = metadata.ProjectUrl,
                                                Source = SourceRegistry,
                                                Confidence = ScanConfidence.Medium
                                            },
                (var _, var _, true) => new DocUrlResolution
                                            {
                                                DocUrl = gitHubUrl,
                                                Source = SourceGithubRepo,
                                                Confidence = ScanConfidence.Medium
                                            },
                var _ => await mCommonPatterns.TryCommonPatternsAsync(metadata.PackageId, ct)
            };

        return res;
    }

    private static bool IsGitHub(string url)
    {
        var res = false;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            res = uri.Host.Equals(GitHubHost, StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.EndsWith("." + GitHubHost, StringComparison.OrdinalIgnoreCase);
        }

        return res;
    }

    private static bool TryGetGitHubUrl(PackageMetadata metadata, out string url)
    {
        string repoUrl = metadata.RepositoryUrl;
        string projectUrl = metadata.ProjectUrl;

        url = (IsGitHub(repoUrl), IsGitHub(projectUrl)) switch
            {
                (true, var _) => repoUrl,
                (var _, true) => projectUrl,
                var _ => string.Empty
            };

        bool res = url.Length > 0;
        return res;
    }

    private const string PipEcosystemId = "pip";
    private const string SourceRegistry = "registry";
    private const string SourceGithubRepo = "github-repo";
    private const string SourceNone = "none";
    private const string GitHubHost = "github.com";
}
