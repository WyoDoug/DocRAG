// NpmRegistryClient.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.Text.Json;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Ecosystems.Npm;

/// <summary>
///     Fetches package metadata from the npm registry.
/// </summary>
public sealed class NpmRegistryClient : IPackageRegistryClient
{
    /// <summary>
    ///     Initializes a new instance of <see cref="NpmRegistryClient" />.
    /// </summary>
    public NpmRegistryClient(IHttpClientFactory httpClientFactory)
    {
        mHttpClientFactory = httpClientFactory;
    }

    private readonly IHttpClientFactory mHttpClientFactory;

    /// <summary>
    ///     The package ecosystem identifier for npm.
    /// </summary>
    public string EcosystemId => EcosystemIdValue;

    /// <summary>
    ///     Fetches metadata for the specified npm package and version.
    ///     Returns <see langword="null" /> when the package is not found.
    /// </summary>
    public async Task<PackageMetadata?> FetchMetadataAsync(string packageId,
                                                           string version,
                                                           CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(packageId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        string cleanVersion = version.TrimStart(smVersionRangePrefixes);
        var url = $"{RegistryBase}/{packageId}/{cleanVersion}";

        var client = mHttpClientFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(TimeoutSeconds);

        using var response = await client.GetAsync(url, ct).ConfigureAwait(continueOnCapturedContext: false);

        PackageMetadata? result = null;

        if (response.IsSuccessStatusCode)
        {
            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
            result = ParseMetadata(packageId, cleanVersion, json);
        }

        return result;
    }

    private static PackageMetadata ParseMetadata(string packageId, string version, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string homepage = root.TryGetProperty(JsonFieldHomepage, out var homepageProp)
                              ? homepageProp.GetString() ?? string.Empty
                              : string.Empty;

        string description = root.TryGetProperty(JsonFieldDescription, out var descProp)
                                 ? descProp.GetString() ?? string.Empty
                                 : string.Empty;

        string repoUrl = ExtractRepositoryUrl(root);

        return new PackageMetadata
                   {
                       PackageId = packageId,
                       Version = version,
                       EcosystemId = EcosystemIdValue,
                       ProjectUrl = homepage,
                       RepositoryUrl = repoUrl,
                       Description = description
                   };
    }

    private static string ExtractRepositoryUrl(JsonElement root)
    {
        var raw = string.Empty;

        if (root.TryGetProperty(JsonFieldRepository, out var repoProp))
        {
            raw = repoProp.ValueKind switch
                {
                    JsonValueKind.String => repoProp.GetString() ?? string.Empty,
                    JsonValueKind.Object => repoProp.TryGetProperty(JsonFieldUrl, out var urlProp)
                                                ? urlProp.GetString() ?? string.Empty
                                                : string.Empty,
                    var _ => string.Empty
                };
        }

        return NormalizeGitUrl(raw);
    }

    private static string NormalizeGitUrl(string url)
    {
        string result = url;
        result = result.Replace(GitPlusHttpsPrefix, HttpsPrefix);
        result = result.Replace(GitProtocolPrefix, HttpsPrefix);

        if (result.EndsWith(GitSuffix, StringComparison.OrdinalIgnoreCase))
            result = result[..^4];

        return result;
    }

    private const string RegistryBase = "https://registry.npmjs.org";
    private const string HttpClientName = "npm";
    private const int TimeoutSeconds = 5;
    private static readonly char[] smVersionRangePrefixes = ['^', '~', '>', '=', '<'];
    private const string EcosystemIdValue = "npm";
    private const string JsonFieldHomepage = "homepage";
    private const string JsonFieldDescription = "description";
    private const string JsonFieldRepository = "repository";
    private const string JsonFieldUrl = "url";
    private const string GitPlusHttpsPrefix = "git+https://";
    private const string HttpsPrefix = "https://";
    private const string GitProtocolPrefix = "git://";
    private const string GitSuffix = ".git";
}
