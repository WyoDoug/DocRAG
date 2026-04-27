// NpmDocUrlResolver.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using DocRAG.Ingestion.Ecosystems.Common;

#endregion

namespace DocRAG.Ingestion.Ecosystems.Npm;

/// <summary>
///     Resolves documentation URLs for npm packages using a confidence-ranked cascade.
/// </summary>
public sealed class NpmDocUrlResolver : IDocUrlResolver
{
    /// <summary>
    ///     Initializes a new instance of <see cref="NpmDocUrlResolver" />.
    /// </summary>
    public NpmDocUrlResolver(CommonDocUrlPatterns commonPatterns)
    {
        mCommonPatterns = commonPatterns;
    }

    private readonly CommonDocUrlPatterns mCommonPatterns;

    /// <summary>
    ///     The package ecosystem identifier for npm.
    /// </summary>
    public string EcosystemId => NpmEcosystemId;

    /// <summary>
    ///     Resolves the best available documentation URL for the given npm package metadata.
    ///     Cascade: (1) non-GitHub homepage â†’ High, (2) repository URL â†’ Medium, (3) common patterns â†’ Low.
    /// </summary>
    public async Task<DocUrlResolution> ResolveAsync(PackageMetadata metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        bool hasHomepage = !string.IsNullOrEmpty(metadata.ProjectUrl);
        bool homepageIsGitHub = hasHomepage && CommonDocUrlPatterns.IsGitHubRepo(metadata.ProjectUrl);
        bool useHomepage = hasHomepage && !homepageIsGitHub;
        bool hasRepo = !string.IsNullOrEmpty(metadata.RepositoryUrl);

        var syncResult = (useHomepage, hasRepo) switch
            {
                (true, var _) => new DocUrlResolution
                                     {
                                         DocUrl = metadata.ProjectUrl,
                                         Source = RegistrySource,
                                         Confidence = ScanConfidence.High
                                     },
                (false, true) => new DocUrlResolution
                                     {
                                         DocUrl = metadata.RepositoryUrl,
                                         Source = GitHubRepoSource,
                                         Confidence = ScanConfidence.Medium
                                     },
                var _ => null
            };

        var res = syncResult ??
                  await mCommonPatterns.TryCommonPatternsAsync(metadata.PackageId, ct)
                                       .ConfigureAwait(continueOnCapturedContext: false);

        return res;
    }

    private const string NpmEcosystemId = "npm";
    private const string RegistrySource = "registry";
    private const string GitHubRepoSource = "github-repo";
}
