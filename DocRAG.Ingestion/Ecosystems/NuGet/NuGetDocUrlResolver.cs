// NuGetDocUrlResolver.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using DocRAG.Ingestion.Ecosystems.Common;

#endregion

namespace DocRAG.Ingestion.Ecosystems.NuGet;

/// <summary>
///     Resolves documentation URLs for NuGet packages using a confidence-ranked cascade.
/// </summary>
public class NuGetDocUrlResolver : IDocUrlResolver
{
    /// <summary>
    ///     Initializes a new instance of <see cref="NuGetDocUrlResolver" />.
    /// </summary>
    public NuGetDocUrlResolver(CommonDocUrlPatterns commonPatterns)
    {
        mCommonPatterns = commonPatterns;
    }

    private readonly CommonDocUrlPatterns mCommonPatterns;

    /// <inheritdoc />
    public string EcosystemId => EcosystemIdentifier;

    /// <inheritdoc />
    public async Task<DocUrlResolution> ResolveAsync(PackageMetadata metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var res = await ResolveInternalAsync(metadata, ct).ConfigureAwait(continueOnCapturedContext: false);
        return res;
    }

    private async Task<DocUrlResolution> ResolveInternalAsync(PackageMetadata metadata, CancellationToken ct)
    {
        DocUrlResolution res;

        bool hasDocUrl = !string.IsNullOrEmpty(metadata.DocumentationUrl);
        bool docUrlIsNotGitHub = hasDocUrl && !CommonDocUrlPatterns.IsGitHubRepo(metadata.DocumentationUrl);

        if (docUrlIsNotGitHub)
        {
            res = new DocUrlResolution
                      {
                          DocUrl = metadata.DocumentationUrl,
                          Source = RegistrySource,
                          Confidence = ScanConfidence.High
                      };
        }
        else
        {
            bool hasProjectUrl = !string.IsNullOrEmpty(metadata.ProjectUrl);
            bool projectUrlIsNotGitHub = hasProjectUrl && !CommonDocUrlPatterns.IsGitHubRepo(metadata.ProjectUrl);

            if (projectUrlIsNotGitHub)
            {
                bool reachable = await mCommonPatterns.IsReachableAsync(metadata.ProjectUrl, ct)
                                                      .ConfigureAwait(continueOnCapturedContext: false);
                res = reachable
                          ? new DocUrlResolution
                                {
                                    DocUrl = metadata.ProjectUrl,
                                    Source = RegistrySource,
                                    Confidence = ScanConfidence.Medium
                                }
                          : await ResolveFromGitHubOrFallbackAsync(metadata, ct)
                                .ConfigureAwait(continueOnCapturedContext: false);
            }
            else
                res = await ResolveFromGitHubOrFallbackAsync(metadata, ct)
                          .ConfigureAwait(continueOnCapturedContext: false);
        }

        return res;
    }

    private async Task<DocUrlResolution> ResolveFromGitHubOrFallbackAsync(
        PackageMetadata metadata,
        CancellationToken ct)
    {
        DocUrlResolution res;

        string gitHubUrl = ResolveGitHubUrl(metadata);
        bool hasGitHub = !string.IsNullOrEmpty(gitHubUrl);

        if (hasGitHub)
        {
            res = new DocUrlResolution
                      {
                          DocUrl = gitHubUrl,
                          Source = GitHubRepoSource,
                          Confidence = ScanConfidence.Medium
                      };
        }
        else
            res = await mCommonPatterns.TryCommonPatternsAsync(metadata.PackageId, ct)
                                       .ConfigureAwait(continueOnCapturedContext: false);

        return res;
    }

    private static string ResolveGitHubUrl(PackageMetadata metadata)
    {
        var res = string.Empty;

        bool repoIsGitHub = !string.IsNullOrEmpty(metadata.RepositoryUrl) &&
                            CommonDocUrlPatterns.IsGitHubRepo(metadata.RepositoryUrl);

        if (repoIsGitHub)
            res = metadata.RepositoryUrl;
        else
        {
            bool projectIsGitHub = !string.IsNullOrEmpty(metadata.ProjectUrl) &&
                                   CommonDocUrlPatterns.IsGitHubRepo(metadata.ProjectUrl);

            if (projectIsGitHub)
                res = metadata.ProjectUrl;
        }

        return res;
    }

    private const string EcosystemIdentifier = "nuget";
    private const string RegistrySource = "registry";
    private const string GitHubRepoSource = "github-repo";
}
