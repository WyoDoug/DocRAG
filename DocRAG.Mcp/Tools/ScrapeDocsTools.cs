// ScrapeDocsTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using DocRAG.Core.Enums;
using DocRAG.Core.Models;
using DocRAG.Database.Repositories;
using DocRAG.Ingestion;
using DocRAG.Ingestion.Scanning;
using ModelContextProtocol.Server;

#endregion

namespace DocRAG.Mcp.Tools;

/// <summary>
///     MCP tools for on-demand documentation scraping and
///     project dependency indexing.
/// </summary>
[McpServerToolType]
public static class ScrapeDocsTools
{
    /// <summary>
    ///     Scrape documentation from a URL with cache awareness and optional pattern overrides.
    ///     Supports resuming prior scrapes by reusing stored job configuration.
    /// </summary>
    [McpServerTool(Name = "scrape_docs")]
    [Description("Scrape documentation from a URL. Cache-aware: returns AlreadyCached unless force=true. " +
                 "Pass allowedUrlPatterns / excludedUrlPatterns only if the auto-derived host filter is too " +
                 "narrow or too broad. Use this for both ad-hoc URLs and post-recon scrapes — there is no " +
                 "separate scrape_library tool. resume=true reuses the most recent ScrapeJob's rootUrl and " +
                 "patterns when url is omitted."
                )]
    public static async Task<string> ScrapeDocs(ScrapeJobRunner runner,
                                                RepositoryFactory repositoryFactory,
                                                [Description("Root URL of the documentation site (optional when resume=true)")]
                                                string? url = null,
                                                [Description("Unique library identifier for cache key")]
                                                string libraryId = "",
                                                [Description("Version string for cache key")]
                                                string version = "",
                                                [Description("Human-readable hint about what this library is")]
                                                string? hint = null,
                                                [Description("Maximum pages to crawl (0 = unlimited, default)")]
                                                int maxPages = DefaultMaxPages,
                                                [Description("Delay between fetches in ms (default 500)")]
                                                int fetchDelayMs = 500,
                                                [Description("Re-scrape even if already cached")]
                                                bool force = false,
                                                [Description("Optional URL patterns (regex) to allow. Defaults to the rootUrl host when omitted.")]
                                                string[]? allowedUrlPatterns = null,
                                                [Description("Optional URL patterns (regex) to exclude.")]
                                                string[]? excludedUrlPatterns = null,
                                                [Description("Resume the most recent scrape for this (libraryId, version), reusing its RootUrl/patterns")]
                                                bool resume = false,
                                                [Description("Optional database profile name")]
                                                string? profile = null,
                                                CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        if (!resume && string.IsNullOrEmpty(url))
            throw new ArgumentException("url is required when resume=false");

        var libraryRepo = repositoryFactory.GetLibraryRepository(profile);

        string json = string.Empty;
        ScrapeJob? jobToQueue = null;
        bool earlyResponseEmitted = false;

        if (resume)
        {
            var jobRepo = repositoryFactory.GetScrapeJobRepository(profile);
            var recent = await jobRepo.ListRecentAsync(limit: 100, ct);
            var previous = recent.Where(j => j.Job.LibraryId == libraryId && j.Job.Version == version)
                                 .OrderByDescending(j => j.CreatedAt)
                                 .FirstOrDefault();

            if (previous == null)
            {
                var noPrior = new
                                  {
                                      Status = StatusNoPriorJob,
                                      Message = $"resume=true but no previous scrape job exists for {libraryId} v{version}. Pass url to start a fresh scrape."
                                  };
                json = JsonSerializer.Serialize(noPrior, new JsonSerializerOptions { WriteIndented = true });
                earlyResponseEmitted = true;
            }
            else
            {
                var versionRecord = await libraryRepo.GetVersionAsync(libraryId, version, ct);
                if (versionRecord != null && versionRecord.Suspect)
                {
                    var refused = new
                                      {
                                          Status = StatusRefused,
                                          Reason = ReasonUrlSuspect,
                                          SuspectReasons = versionRecord.SuspectReasons,
                                          Hint = "Call submit_url_correction(library, version, newUrl) with a corrected URL."
                                      };
                    json = JsonSerializer.Serialize(refused, new JsonSerializerOptions { WriteIndented = true });
                    earlyResponseEmitted = true;
                }
                else
                {
                    jobToQueue = new ScrapeJob
                                     {
                                         RootUrl = url ?? previous.Job.RootUrl,
                                         LibraryId = libraryId,
                                         Version = version,
                                         LibraryHint = hint ?? previous.Job.LibraryHint,
                                         AllowedUrlPatterns = allowedUrlPatterns ?? previous.Job.AllowedUrlPatterns,
                                         ExcludedUrlPatterns = excludedUrlPatterns ?? previous.Job.ExcludedUrlPatterns,
                                         MaxPages = maxPages,
                                         FetchDelayMs = fetchDelayMs,
                                         ForceClean = force
                                     };
                }
            }
        }

        if (!earlyResponseEmitted)
        {
            var existingVersion = await libraryRepo.GetVersionAsync(libraryId, version, ct);
            if (existingVersion != null && !force)
            {
                var cached = new
                                 {
                                     Status = StatusAlreadyCached,
                                     LibraryId = libraryId,
                                     Version = version,
                                     Message = $"Documentation for {libraryId} v{version} is already indexed " +
                                               $"({existingVersion.ChunkCount} chunks). Use force=true to re-scrape."
                                 };
                json = JsonSerializer.Serialize(cached, new JsonSerializerOptions { WriteIndented = true });
            }
            else
            {
                string resolvedUrl = url ?? string.Empty;
                jobToQueue ??= BuildJobForUrl(resolvedUrl, libraryId, version, hint, maxPages, fetchDelayMs, force, allowedUrlPatterns, excludedUrlPatterns);
                var jobId = await runner.QueueAsync(jobToQueue, profile, ct);
                var response = new
                                   {
                                       JobId = jobId,
                                       Status = nameof(ScrapeJobStatus.Queued),
                                       LibraryId = libraryId,
                                       Version = version,
                                       Message = $"Scrape job queued. Poll get_scrape_status with jobId='{jobId}' for progress."
                                   };
                json = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
            }
        }

        return json;
    }

    private static ScrapeJob BuildJobForUrl(string url,
                                            string libraryId,
                                            string version,
                                            string? hint,
                                            int maxPages,
                                            int fetchDelayMs,
                                            bool force,
                                            string[]? allowedUrlPatterns,
                                            string[]? excludedUrlPatterns)
    {
        ScrapeJob job;
        if (allowedUrlPatterns != null || excludedUrlPatterns != null)
        {
            job = new ScrapeJob
                      {
                          RootUrl = url,
                          LibraryId = libraryId,
                          Version = version,
                          LibraryHint = hint ?? string.Empty,
                          AllowedUrlPatterns = allowedUrlPatterns ?? [new Uri(url).Host],
                          ExcludedUrlPatterns = excludedUrlPatterns ?? [],
                          MaxPages = maxPages,
                          FetchDelayMs = fetchDelayMs,
                          ForceClean = force
                      };
        }
        else
        {
            job = ScrapeJobFactory.CreateFromUrl(url, libraryId, version, hint, maxPages, fetchDelayMs, forceClean: force);
        }
        return job;
    }


    /// <summary>
    ///     Scan a project to discover all package dependencies and scrape their docs.
    /// </summary>
    [McpServerTool(Name = "index_project_dependencies")]
    [Description("Scan a project to discover all package dependencies (NuGet, npm, pip), " +
                 "resolve their documentation URLs, and scrape everything not already cached. " +
                 "Pass a directory path to auto-detect project files, or a specific " +
                 ".sln/.csproj/package.json/requirements.txt/pyproject.toml file. " +
                 "Returns a report showing what was found, cached, queued, and unresolved."
                )]
    public static async Task<string> IndexProjectDependencies(DependencyIndexer indexer,
                                                              [Description("Project root directory or specific project file path"
                                                                          )]
                                                              string path,
                                                              [Description("Optional database profile name")]
                                                              string? profile = null,
                                                              CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var report = await indexer.IndexProjectAsync(path, profile, ct);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        return json;
    }

    private const string StatusAlreadyCached = "AlreadyCached";
    private const string StatusNoPriorJob = "NoPriorJob";
    private const string StatusRefused = "Refused";
    private const string ReasonUrlSuspect = "URL_SUSPECT";
    private const int DefaultMaxPages = 0;
}
