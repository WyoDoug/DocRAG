// PyPiRegistryClient.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.Text.Json;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;

#endregion

namespace DocRAG.Ingestion.Ecosystems.Pip;

/// <summary>
///     Fetches package metadata from the PyPI JSON API.
/// </summary>
public sealed class PyPiRegistryClient : IPackageRegistryClient
{
    /// <summary>
    ///     Initializes a new instance of <see cref="PyPiRegistryClient" />.
    /// </summary>
    public PyPiRegistryClient(IHttpClientFactory httpClientFactory)
    {
        mHttpClientFactory = httpClientFactory;
    }

    private readonly IHttpClientFactory mHttpClientFactory;

    /// <inheritdoc />
    public string EcosystemId => PipEcosystemId;

    /// <inheritdoc />
    public async Task<PackageMetadata?> FetchMetadataAsync(string packageId,
                                                           string version,
                                                           CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(packageId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        string url = string.IsNullOrEmpty(version)
                         ? $"{PyPiBaseUrl}/{packageId}/json"
                         : $"{PyPiBaseUrl}/{packageId}/{version}/json";

        var client = mHttpClientFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(TimeoutSeconds);

        using var response = await client.GetAsync(url, ct);

        PackageMetadata? res = null;

        if (response.IsSuccessStatusCode)
        {
            using var doc =
                await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            res = BuildMetadata(packageId, doc.RootElement);
        }

        return res;
    }

    private PackageMetadata BuildMetadata(string packageId, JsonElement root)
    {
        var info = root.GetProperty(InfoKey);

        string resolvedVersion = info.TryGetProperty(VersionKey, out var versionEl)
                                     ? versionEl.GetString() ?? string.Empty
                                     : string.Empty;

        string summary = info.TryGetProperty(SummaryKey, out var summaryEl)
                             ? summaryEl.GetString() ?? string.Empty
                             : string.Empty;

        string homePage = info.TryGetProperty(HomePageKey, out var homePageEl)
                              ? homePageEl.GetString() ?? string.Empty
                              : string.Empty;

        var docUrl = string.Empty;
        var repoUrl = string.Empty;
        string projectUrl = homePage;

        if (info.TryGetProperty(ProjectUrlsKey, out var projectUrls) && projectUrls.ValueKind == JsonValueKind.Object)
        {
            docUrl = ExtractProjectUrl(projectUrls, DocUrlKeyDocumentation, DocUrlKeyDocs);
            repoUrl = ExtractProjectUrl(projectUrls, DocUrlKeySource, DocUrlKeyRepository, DocUrlKeySourceCode);

            if (string.IsNullOrEmpty(projectUrl))
                projectUrl = ExtractProjectUrl(projectUrls, DocUrlKeyHomepage);
        }

        return new PackageMetadata
                   {
                       PackageId = packageId,
                       Version = resolvedVersion,
                       EcosystemId = PipEcosystemId,
                       ProjectUrl = projectUrl,
                       RepositoryUrl = repoUrl,
                       DocumentationUrl = docUrl,
                       Description = summary
                   };
    }

    private static string ExtractProjectUrl(JsonElement projectUrls, params string[] keys)
    {
        var res = string.Empty;

        foreach(string key in keys.Where(k => projectUrls.TryGetProperty(k, out var _)))
        {
            projectUrls.TryGetProperty(key, out var el);
            res = el.GetString() ?? string.Empty;
            break;
        }

        return res;
    }

    private const string PipEcosystemId = "pip";
    private const string InfoKey = "info";
    private const string VersionKey = "version";
    private const string SummaryKey = "summary";
    private const string HomePageKey = "home_page";
    private const string ProjectUrlsKey = "project_urls";
    private const string HttpClientName = "PyPI";
    private const string PyPiBaseUrl = "https://pypi.org/pypi";
    private const int TimeoutSeconds = 5;
    private const string DocUrlKeyDocumentation = "Documentation";
    private const string DocUrlKeyDocs = "Docs";
    private const string DocUrlKeySource = "Source";
    private const string DocUrlKeyRepository = "Repository";
    private const string DocUrlKeySourceCode = "Source Code";
    private const string DocUrlKeyHomepage = "Homepage";
}
