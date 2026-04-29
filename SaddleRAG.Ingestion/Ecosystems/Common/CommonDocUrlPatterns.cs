// CommonDocUrlPatterns.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.Text.RegularExpressions;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using Microsoft.Extensions.Logging;

#endregion

namespace SaddleRAG.Ingestion.Ecosystems.Common;

/// <summary>
///     Shared fallback logic for doc URL resolution using well-known hosting patterns.
/// </summary>
public class CommonDocUrlPatterns
{
    /// <summary>
    ///     Initializes a new instance of <see cref="CommonDocUrlPatterns" />.
    /// </summary>
    public CommonDocUrlPatterns(IHttpClientFactory httpClientFactory, ILogger<CommonDocUrlPatterns> logger)
    {
        mHttpClientFactory = httpClientFactory;
        mLogger = logger;
    }

    private readonly IHttpClientFactory mHttpClientFactory;
    private readonly ILogger<CommonDocUrlPatterns> mLogger;

    /// <summary>
    ///     Returns <see langword="true" /> if the given URL points to a GitHub repository.
    /// </summary>
    public static bool IsGitHubRepo(string url)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);

        bool res = smGitHubPattern.IsMatch(url);
        return res;
    }

    /// <summary>
    ///     Tries well-known documentation hosting patterns for the given package name.
    ///     Returns the first reachable URL, or a resolution with <see langword="null" /> DocUrl if none found.
    /// </summary>
    public async Task<DocUrlResolution> TryCommonPatternsAsync(string packageId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(packageId);

        string name = packageId.ToLowerInvariant();
        string[] candidates =
            [
                string.Format(ReadTheDocsTemplate, name),
                string.Format(DocsSubdomainTemplate, name),
                string.Format(GitHubPagesTemplate, name),
                string.Format(DevDomainTemplate, name)
            ];

        DocUrlResolution res = new DocUrlResolution
                                   {
                                       DocUrl = null,
                                       Source = NoneSource,
                                       Confidence = ScanConfidence.Low
                                   };

        foreach(string candidate in candidates)
        {
            bool reachable = await IsReachableAsync(candidate, ct).ConfigureAwait(continueOnCapturedContext: false);
            if (reachable)
            {
                res = new DocUrlResolution
                          {
                              DocUrl = candidate,
                              Source = PatternSource,
                              Confidence = ScanConfidence.Low
                          };
                break;
            }
        }

        return res;
    }

    /// <summary>
    ///     Probes the given URL with an HTTP HEAD request.
    ///     Returns <see langword="true" /> if the server responds with a success status code.
    /// </summary>
    public async Task<bool> IsReachableAsync(string url, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);

        var res = false;

        try
        {
            using var client = mHttpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(HttpTimeoutSeconds);

            using var response = await client
                                       .SendAsync(new HttpRequestMessage(HttpMethod.Head, url), ct)
                                       .ConfigureAwait(continueOnCapturedContext: false);

            res = response.IsSuccessStatusCode;
        }
        catch(Exception ex)
        {
            mLogger.LogDebug(ex, "HEAD probe failed for {Url}", url);
        }

        return res;
    }

    private const int HttpTimeoutSeconds = 5;
    private const string ReadTheDocsTemplate = "https://{0}.readthedocs.io";
    private const string DocsSubdomainTemplate = "https://docs.{0}.com";
    private const string GitHubPagesTemplate = "https://{0}.github.io";
    private const string DevDomainTemplate = "https://{0}.dev";
    private const string PatternSource = "pattern";
    private const string NoneSource = "none";

    private const string GitHubDomainPattern = @"github\.com";

    private static readonly Regex smGitHubPattern =
        new Regex(GitHubDomainPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
