// IngestionTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using DocRAG.Core.Enums;
using DocRAG.Core.Models;
using DocRAG.Database.Repositories;
using DocRAG.Ingestion;
using DocRAG.Ingestion.Crawling;
using ModelContextProtocol.Server;

#endregion

namespace DocRAG.Mcp.Tools;

/// <summary>
///     MCP tools for triggering ingestion and reloading vector indices
///     without restarting the server.
/// </summary>
[McpServerToolType]
public static class IngestionTools
{
    [McpServerTool(Name = "dryrun_scrape")]
    [Description("Dry-run a documentation scrape â€” fetches every page with Playwright " +
                 "but does NOT store anything to the database or clone any GitHub repos. " +
                 "Returns a report showing how many pages would be ingested, how deep " +
                 "the crawl goes, and which GitHub repos would be cloned. " +
                 "Use this BEFORE running scrape_library on a new library to verify " +
                 "the URL patterns are correct and the crawl scope is reasonable."
                )]
    public static async Task<string> DryRunScrape(PageCrawler crawler,
                                                  [Description("Root URL to begin crawling from")]
                                                  string rootUrl,
                                                  [Description("Allowed URL patterns (regex). Defaults to the rootUrl host."
                                                              )]
                                                  string[]? allowedUrlPatterns = null,
                                                  [Description("Excluded URL patterns (regex)")]
                                                  string[]? excludedUrlPatterns = null,
                                                  [Description("Max pages to fetch in dry run, 0 = unlimited")]
                                                  int maxPages = DefaultDryRunMaxPages,
                                                  [Description("Delay between fetches in ms")]
                                                  int fetchDelayMs = 500,
                                                  [Description("Max depth for same-host pages outside the root path")]
                                                  int sameHostDepth = 5,
                                                  [Description("Max depth for pages on a different host entirely; 0 disables off-site crawling"
                                                              )]
                                                  int offSiteDepth = 1,
                                                  CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(crawler);
        ArgumentException.ThrowIfNullOrEmpty(rootUrl);

        var allowed = allowedUrlPatterns ?? [new Uri(rootUrl).Host];

        var job = new ScrapeJob
                      {
                          RootUrl = rootUrl,
                          LibraryId = "dryrun",
                          Version = "dryrun",
                          LibraryHint = "Dry run",
                          AllowedUrlPatterns = allowed,
                          ExcludedUrlPatterns = excludedUrlPatterns ?? [],
                          MaxPages = maxPages,
                          FetchDelayMs = fetchDelayMs,
                          SameHostDepth = sameHostDepth,
                          OffSiteDepth = offSiteDepth
                      };

        var report = await crawler.DryRunAsync(job, ct);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        return json;
    }

    [McpServerTool(Name = "scrape_library")]
    [Description("Queue a documentation library scrape. Returns immediately with a job id. " +
                 "Poll get_scrape_status to check progress. " +
                 "When complete, the new chunks are automatically indexed and searchable via search_docs."
                )]
    public static async Task<string> ScrapeLibrary(ScrapeJobRunner runner,
                                                   [Description("Root URL to begin crawling from")]
                                                   string rootUrl,
                                                   [Description("Unique library identifier (e.g. 'questpdf', 'infragistics-wpf')"
                                                               )]
                                                   string libraryId,
                                                   [Description("Version string for this scrape (e.g. '2025.3')")]
                                                   string version,
                                                   [Description("Human-readable hint about what this library is")]
                                                   string hint,
                                                   [Description("Allowed URL patterns (regex). Defaults to the rootUrl host."
                                                               )]
                                                   string[]? allowedUrlPatterns = null,
                                                   [Description("Excluded URL patterns (regex)")]
                                                   string[]? excludedUrlPatterns = null,
                                                   [Description("Max pages to crawl, 0 = unlimited (default)")]
                                                   int maxPages = 0,
                                                   [Description("Delay between fetches in ms")]
                                                   int fetchDelayMs = ScrapeJob.DefaultFetchDelayMs,
                                                   [Description("Optional database profile name")]
                                                   string? profile = null,
                                                   CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrEmpty(rootUrl);
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(hint);

        var allowed = allowedUrlPatterns ?? [new Uri(rootUrl).Host];

        var job = new ScrapeJob
                      {
                          RootUrl = rootUrl,
                          LibraryId = libraryId,
                          Version = version,
                          LibraryHint = hint,
                          AllowedUrlPatterns = allowed,
                          ExcludedUrlPatterns = excludedUrlPatterns ?? [],
                          MaxPages = maxPages,
                          FetchDelayMs = fetchDelayMs
                      };

        var jobId = await runner.QueueAsync(job, profile, ct);

        var response = new
                           {
                               JobId = jobId,
                               Status = nameof(ScrapeJobStatus.Queued),
                               Message = $"Scrape job queued. Poll get_scrape_status with jobId='{jobId}' for progress."
                           };

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        return json;
    }

    [McpServerTool(Name = "get_scrape_status")]
    [Description("Check the status of a scrape job by its id.")]
    public static async Task<string> GetScrapeStatus(RepositoryFactory repositoryFactory,
                                                     [Description("Job id returned from scrape_library")]
                                                     string jobId,
                                                     [Description("Optional database profile name")]
                                                     string? profile = null,
                                                     CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        var jobRepo = repositoryFactory.GetScrapeJobRepository(profile);
        var job = await jobRepo.GetAsync(jobId, ct);

        string result;
        if (job == null)
            result = $"No scrape job found with id '{jobId}'.";
        else
        {
            var response = new
                               {
                                   job.Id,
                                   job.Status,
                                   job.PipelineState,
                                   job.PagesQueued,
                                   job.PagesFetched,
                                   job.PagesClassified,
                                   job.ChunksGenerated,
                                   job.ChunksEmbedded,
                                   job.ChunksCompleted,
                                   job.PagesCompleted,
                                   job.ErrorCount,
                                   job.ErrorMessage,
                                   job.CreatedAt,
                                   job.StartedAt,
                                   job.CompletedAt,
                                   Library = job.Job.LibraryId,
                                   job.Job.Version
                               };

            result = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }

        return result;
    }

    [McpServerTool(Name = "list_scrape_jobs")]
    [Description("List recent scrape jobs (most recent first).")]
    public static async Task<string> ListScrapeJobs(RepositoryFactory repositoryFactory,
                                                    [Description("Maximum jobs to return (default 20)")]
                                                    int limit = 20,
                                                    [Description("Optional database profile name")]
                                                    string? profile = null,
                                                    CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);

        var jobRepo = repositoryFactory.GetScrapeJobRepository(profile);
        var jobs = await jobRepo.ListRecentAsync(limit, ct);

        var response = jobs.Select(j => new
                                            {
                                                j.Id,
                                                j.Status,
                                                j.PipelineState,
                                                Library = j.Job.LibraryId,
                                                j.Job.Version,
                                                j.CreatedAt,
                                                j.CompletedAt
                                            }
                                  );

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        return json;
    }

    [McpServerTool(Name = "reload_profile")]
    [Description("Reload the in-memory vector index from MongoDB for a profile. " +
                 "Useful after manual data changes or to recover from index drift. " +
                 "Normally not needed â€” scrape_library auto-reloads when ingestion completes."
                )]
    public static async Task<string> ReloadProfile(ScrapeJobRunner runner,
                                                   [Description("Optional database profile name")]
                                                   string? profile = null,
                                                   CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runner);

        await runner.ReloadProfileAsync(profile, ct);
        return $"Reloaded vector index for profile '{profile ?? "(default)"}'.";
    }

    private const int DefaultDryRunMaxPages = 200;
    private const int DefaultScrapeMaxPages = 1000;
}
