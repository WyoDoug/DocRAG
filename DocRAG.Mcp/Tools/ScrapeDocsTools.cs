// // ScrapeDocsTools.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using DocRAG.Core.Enums;
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
    ///     Scrape documentation from a URL with auto-derived crawl settings.
    ///     Checks cache first and returns immediately if already indexed.
    /// </summary>
    [McpServerTool(Name = "scrape_docs")]
    [Description("Scrape documentation from a URL with auto-derived crawl settings. " +
                 "Just provide the URL and a library identifier â€” the system figures out " +
                 "scope, depth limits, and exclusion patterns automatically. " +
                 "Use this for ad-hoc documentation sites, vendor SDKs, or any URL " +
                 "that isn't a package manager dependency. " +
                 "Checks cache first â€” won't re-scrape if already indexed unless force=true."
                )]
    public static async Task<string> ScrapeDocs(ScrapeJobRunner runner,
                                                RepositoryFactory repositoryFactory,
                                                [Description("Root URL of the documentation site")]
                                                string url,
                                                [Description("Unique library identifier for cache key")]
                                                string libraryId,
                                                [Description("Version string for cache key")]
                                                string version,
                                                [Description("Human-readable hint about what this library is")]
                                                string? hint = null,
                                                [Description("Maximum pages to crawl (0 = unlimited, default)")]
                                                int maxPages = DefaultMaxPages,
                                                [Description("Delay between fetches in ms (default 500)")]
                                                int fetchDelayMs = 500,
                                                [Description("Re-scrape even if already cached")]
                                                bool force = false,
                                                [Description("Optional database profile name")]
                                                string? profile = null,
                                                CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var libraryRepo = repositoryFactory.GetLibraryRepository(profile);

        string json;
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
            var job = ScrapeJobFactory.CreateFromUrl(url,
                                                     libraryId,
                                                     version,
                                                     hint,
                                                     maxPages,
                                                     fetchDelayMs,
                                                     forceClean: force
                                                    );
            var jobId = await runner.QueueAsync(job, profile, ct);

            var response = new
                               {
                                   JobId = jobId,
                                   Status = nameof(ScrapeJobStatus.Queued),
                                   LibraryId = libraryId,
                                   Version = version,
                                   Message =
                                       $"Scrape job queued. Poll get_scrape_status with jobId='{jobId}' for progress."
                               };
            json = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }

        return json;
    }

    /// <summary>
    ///     Continue a previously interrupted or MaxPages-limited scrape.
    ///     Retrieves the original job config and resumes from where it left off.
    /// </summary>
    [McpServerTool(Name = "continue_scrape")]
    [Description("Continue a previously interrupted or MaxPages-limited scrape. " +
                 "Retrieves the original job configuration from the most recent scrape " +
                 "for this library+version and resumes crawling from where it left off â€” " +
                 "already-indexed pages are skipped automatically."
                )]
    public static async Task<string> ContinueScrape(ScrapeJobRunner runner,
                                                    RepositoryFactory repositoryFactory,
                                                    [Description("Library identifier to continue scraping")]
                                                    string libraryId,
                                                    [Description("Version string to continue scraping")]
                                                    string version,
                                                    [Description("Optional database profile name")]
                                                    string? profile = null,
                                                    CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var jobRepo = repositoryFactory.GetScrapeJobRepository(profile);
        var recentJobs = await jobRepo.ListRecentAsync(limit: 100, ct);
        var previousJob = recentJobs
                          .Where(j => j.Job.LibraryId == libraryId && j.Job.Version == version)
                          .OrderByDescending(j => j.CreatedAt)
                          .FirstOrDefault();

        string json;
        if (previousJob == null)
        {
            var notFound = new
                               {
                                   Status = StatusNotFound,
                                   Message = $"No previous scrape job found for {libraryId} v{version}. " +
                                             StartNewScrapeMessage
                               };
            json = JsonSerializer.Serialize(notFound, new JsonSerializerOptions { WriteIndented = true });
        }
        else
        {
            var jobId = await runner.QueueAsync(previousJob.Job, profile, ct);

            var response = new
                               {
                                   JobId = jobId,
                                   Status = nameof(ScrapeJobStatus.Queued),
                                   LibraryId = libraryId,
                                   Version = version,
                                   PreviousJobId = previousJob.Id,
                                   Message = $"Resume scrape job queued. Already-indexed pages will be skipped. " +
                                             $"Poll get_scrape_status with jobId='{jobId}' for progress."
                               };
            json = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }

        return json;
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
    private const string StatusNotFound = "NotFound";
    private const string StartNewScrapeMessage = "Use scrape_docs or scrape_library to start a new scrape.";
    private const int DefaultMaxPages = 0;
}
