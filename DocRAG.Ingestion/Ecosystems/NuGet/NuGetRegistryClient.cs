// NuGetRegistryClient.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.Text.Json;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using Microsoft.Extensions.Logging;

#endregion

namespace DocRAG.Ingestion.Ecosystems.NuGet;

/// <summary>
///     Fetches package metadata from the NuGet v3 registration API.
/// </summary>
public class NuGetRegistryClient : IPackageRegistryClient
{
    /// <summary>
    ///     Initializes a new instance of <see cref="NuGetRegistryClient" />.
    /// </summary>
    public NuGetRegistryClient(IHttpClientFactory httpClientFactory, ILogger<NuGetRegistryClient> logger)
    {
        mHttpClientFactory = httpClientFactory;
        mLogger = logger;
    }

    private readonly IHttpClientFactory mHttpClientFactory;
    private readonly ILogger<NuGetRegistryClient> mLogger;

    /// <inheritdoc />
    public string EcosystemId => EcosystemIdentifier;

    /// <inheritdoc />
    public async Task<PackageMetadata?> FetchMetadataAsync(string packageId,
                                                           string version,
                                                           CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(packageId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        string url = string.Format(RegistrationUrlTemplate, packageId.ToLowerInvariant(), version.ToLowerInvariant());
        PackageMetadata? res = null;

        try
        {
            var client = mHttpClientFactory.CreateClient(NuGetClientName);
            client.Timeout = TimeSpan.FromSeconds(HttpTimeoutSeconds);

            string json = await FetchJsonAsync(client, url, ct).ConfigureAwait(continueOnCapturedContext: false);

            if (!string.IsNullOrEmpty(json))
            {
                string catalogJson = await ResolveCatalogEntryAsync(client, json, ct)
                                         .ConfigureAwait(continueOnCapturedContext: false);

                if (!string.IsNullOrEmpty(catalogJson))
                    res = ParseMetadata(packageId, version, catalogJson);
            }
        }
        catch(Exception ex)
        {
            mLogger.LogDebug(ex, "Failed to fetch NuGet metadata for {PackageId} {Version}", packageId, version);
        }

        return res;
    }

    private static async Task<string> FetchJsonAsync(HttpClient client, string url, CancellationToken ct)
    {
        var res = string.Empty;

        using var response = await client.GetAsync(url, ct).ConfigureAwait(continueOnCapturedContext: false);

        if (response.IsSuccessStatusCode)
            res = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(continueOnCapturedContext: false);

        return res;
    }

    private static async Task<string> ResolveCatalogEntryAsync(HttpClient client,
                                                               string registrationJson,
                                                               CancellationToken ct)
    {
        var res = string.Empty;

        using var doc = JsonDocument.Parse(registrationJson);
        var root = doc.RootElement;

        if (root.TryGetProperty(CatalogEntryProperty, out var catalogEntry))
        {
            res = catalogEntry.ValueKind switch
                {
                    JsonValueKind.Object => registrationJson,
                    JsonValueKind.String => await FetchJsonAsync(client, catalogEntry.GetString() ?? string.Empty, ct)
                                                .ConfigureAwait(continueOnCapturedContext: false),
                    var _ => string.Empty
                };
        }

        return res;
    }

    private static PackageMetadata? ParseMetadata(string packageId, string version, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // The catalog entry JSON may have properties at root (when fetched from
        // the catalog URL) or nested under "catalogEntry" (when inline).
        var entry = root.TryGetProperty(CatalogEntryProperty, out var nested) &&
                    nested.ValueKind == JsonValueKind.Object
                        ? nested
                        : root;

        string projectUrl = entry.TryGetProperty(ProjectUrlProperty, out var puEl)
                                ? puEl.GetString() ?? string.Empty
                                : string.Empty;

        string description = entry.TryGetProperty(DescriptionProperty, out var descEl)
                                 ? descEl.GetString() ?? string.Empty
                                 : string.Empty;

        var repositoryUrl = string.Empty;
        if (entry.TryGetProperty(RepositoryProperty, out var repoEl) &&
            repoEl.ValueKind == JsonValueKind.Object &&
            repoEl.TryGetProperty(RepositoryUrlProperty, out var repoUrlEl))
            repositoryUrl = repoUrlEl.GetString() ?? string.Empty;

        var res = new PackageMetadata
                      {
                          PackageId = packageId,
                          Version = version,
                          EcosystemId = "nuget",
                          ProjectUrl = projectUrl,
                          RepositoryUrl = repositoryUrl,
                          Description = description
                      };

        return res;
    }

    private const string EcosystemIdentifier = "nuget";
    private const string NuGetClientName = "NuGet";
    private const string RegistrationUrlTemplate = "https://api.nuget.org/v3/registration5-gz-semver2/{0}/{1}.json";
    private const int HttpTimeoutSeconds = 5;
    private const string CatalogEntryProperty = "catalogEntry";
    private const string ProjectUrlProperty = "projectUrl";
    private const string DescriptionProperty = "description";
    private const string RepositoryProperty = "repository";
    private const string RepositoryUrlProperty = "url";
}
