// PageTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion;
using ModelContextProtocol.Server;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     Page-level inventory and top-up tools. <c>list_pages</c> exposes
///     the URL list for a (library, version) so callers can audit
///     completeness; <c>add_page</c> ingests a single URL into an
///     existing index without re-crawling the whole library.
/// </summary>
[McpServerToolType]
public static class PageTools
{
    [McpServerTool(Name = "list_pages")]
    [Description("List the URLs of every page indexed for a (library, version). Read-only; useful for " +
                 "auditing scrape completeness against a known sitemap or nav, and for deciding which " +
                 "URLs to top up via add_page. Returns Url, Title, FetchedAt per page, plus a Count."
                )]
    public static async Task<string> ListPages(RepositoryFactory repositoryFactory,
                                               [Description("Library identifier")]
                                               string library,
                                               [Description("Specific version — defaults to current")]
                                               string? version = null,
                                               [Description("Optional database profile name")]
                                               string? profile = null,
                                               CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(library);

        var libraryRepo = repositoryFactory.GetLibraryRepository(profile);
        var pageRepo = repositoryFactory.GetPageRepository(profile);

        var lib = await libraryRepo.GetLibraryAsync(library, ct);

        string result;
        if (lib == null)
            result = JsonSerializer.Serialize(new { Error = $"Library '{library}' not found." }, smJsonOptions);
        else
            result = await BuildPageListResponseAsync(library, lib, version, pageRepo, ct);

        return result;
    }

    private static async Task<string> BuildPageListResponseAsync(string library,
                                                                  Core.Models.LibraryRecord lib,
                                                                  string? version,
                                                                  IPageRepository pageRepo,
                                                                  CancellationToken ct)
    {
        var resolvedVersion = version ?? lib.CurrentVersion;
        var pages = await pageRepo.GetPagesAsync(library, resolvedVersion, ct);

        var projection = pages
                         .OrderBy(p => p.Url, StringComparer.OrdinalIgnoreCase)
                         .Select(p => new
                                          {
                                              p.Url,
                                              p.Title,
                                              p.FetchedAt
                                          })
                         .ToList();

        var response = new
                           {
                               library,
                               version = resolvedVersion,
                               count = projection.Count,
                               pages = projection
                           };
        return JsonSerializer.Serialize(response, smJsonOptions);
    }

    [McpServerTool(Name = "add_page")]
    [Description("Fetch a single URL and add it to an existing (library, version) index without " +
                 "re-crawling the library. Goes through the same classify/chunk/embed/index path as " +
                 "a normal scrape, but skips link extraction so we don't BFS off the page. Use this " +
                 "to top up pages the main scrape missed (e.g. WAF-gated docs that need extra retries). " +
                 "Idempotent: skips if the URL is already indexed for this (library, version) unless " +
                 "force=true."
                )]
    public static async Task<string> AddPage(IngestionOrchestrator orchestrator,
                                             RepositoryFactory repositoryFactory,
                                             [Description("Library identifier — must already exist")]
                                             string library,
                                             [Description("Library version — must already exist")]
                                             string version,
                                             [Description("Full URL of the page to fetch and index")]
                                             string url,
                                             [Description("Re-fetch even if URL is already indexed")]
                                             bool force = false,
                                             [Description("Optional database profile name")]
                                             string? profile = null,
                                             CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(url);

        var libraryRepo = repositoryFactory.GetLibraryRepository(profile);
        var pageRepo = repositoryFactory.GetPageRepository(profile);

        var lib = await libraryRepo.GetLibraryAsync(library, ct);
        string result = (lib, lib?.AllVersions.Contains(version) ?? false) switch
            {
                (null, _) => JsonSerializer.Serialize(new { Error = $"Library '{library}' not found." }, smJsonOptions),
                (_, false) => JsonSerializer.Serialize(new { Error = $"Version '{version}' not found for library '{library}'." }, smJsonOptions),
                _ => await DispatchAddPageAsync(orchestrator, pageRepo, library, version, url, force, profile, ct)
            };

        return result;
    }

    private static async Task<string> DispatchAddPageAsync(IngestionOrchestrator orchestrator,
                                                            IPageRepository pageRepo,
                                                            string library,
                                                            string version,
                                                            string url,
                                                            bool force,
                                                            string? profile,
                                                            CancellationToken ct)
    {
        string result;

        var existing = await pageRepo.GetPageByUrlAsync(library, version, url, ct);
        if (existing != null && !force)
        {
            var skipped = new
                              {
                                  Status = StatusSkipped,
                                  Reason = ReasonAlreadyIndexed,
                                  Url = url,
                                  ExistingFetchedAt = existing.FetchedAt
                              };
            result = JsonSerializer.Serialize(skipped, smJsonOptions);
        }
        else
        {
            var outcome = await orchestrator.IngestSinglePageAsync(library, version, url, profile, ct);
            result = JsonSerializer.Serialize(outcome, smJsonOptions);
        }

        return result;
    }

    private static readonly JsonSerializerOptions smJsonOptions = new() { WriteIndented = true };

    private const string StatusSkipped = "Skipped";
    private const string ReasonAlreadyIndexed = "URL is already indexed for this library/version. Pass force=true to re-fetch.";
}
