// HealthTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using DocRAG.Database.Repositories;
using ModelContextProtocol.Server;

#endregion

namespace DocRAG.Mcp.Tools;

/// <summary>
///     MCP tools for diagnostic visibility into a library's index state.
///     get_library_health surfaces chunk count, language mix, hostnames,
///     boundary-issue rate, suspect markers — distinct from
///     get_library_overview, which returns the actual library content.
/// </summary>
[McpServerToolType]
public static class HealthTools
{
    [McpServerTool(Name = "get_library_health")]
    [Description("Per-version diagnostic snapshot. Returns chunk count, hostname " +
                 "distribution, language mix, boundary-issue rate, and suspect markers. " +
                 "Also returns a SuggestedNextAction field (submit_url_correction if suspect, " +
                 "rechunk_library if boundaryIssuePct ≥ 10%, rescrub_library if parser is stale, " +
                 "null if healthy). For the actual library content, use get_library_overview instead."
                )]
    public static async Task<string> GetLibraryHealth(RepositoryFactory repositoryFactory,
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
        var chunkRepo = repositoryFactory.GetChunkRepository(profile);

        var lib = await libraryRepo.GetLibraryAsync(library, ct);
        string result;
        if (lib == null)
            result = JsonSerializer.Serialize(new { Error = $"Library '{library}' not found." }, smJsonOptions);
        else
            result = await BuildHealthResponseAsync(library, lib, version, chunkRepo, libraryRepo, ct);
        return result;
    }

    private static async Task<string> BuildHealthResponseAsync(
        string library,
        LibraryRecord lib,
        string? version,
        IChunkRepository chunkRepo,
        ILibraryRepository libraryRepo,
        CancellationToken ct)
    {
        var resolvedVersion = version ?? lib.CurrentVersion;
        var versionRecord = await libraryRepo.GetVersionAsync(library, resolvedVersion, ct);

        string result;
        if (versionRecord == null)
            result = JsonSerializer.Serialize(new { Error = $"Version '{resolvedVersion}' not found." }, smJsonOptions);
        else
            result = await BuildVersionSnapshotAsync(library, lib, resolvedVersion, versionRecord, chunkRepo, ct);
        return result;
    }

    private static async Task<string> BuildVersionSnapshotAsync(
        string library,
        LibraryRecord lib,
        string resolvedVersion,
        LibraryVersionRecord versionRecord,
        IChunkRepository chunkRepo,
        CancellationToken ct)
    {
        var languageMix = await chunkRepo.GetLanguageMixAsync(library, resolvedVersion, ct);
        var hostnames = await chunkRepo.GetHostnameDistributionAsync(library, resolvedVersion, ct);
        var (boundaryHint, boundaryHintMessage) = ResolveBoundaryHint(versionRecord.BoundaryIssuePct);

        var hostnamesProjection = hostnames.OrderByDescending(kv => kv.Value)
                                           .Take(MaxHostnamesReturned)
                                           .Select(kv => new { host = kv.Key, count = kv.Value })
                                           .ToList();

        var response = new
                           {
                               library,
                               version = resolvedVersion,
                               currentVersion = lib.CurrentVersion,
                               lastScrapedAt = versionRecord.ScrapedAt,
                               chunkCount = versionRecord.ChunkCount,
                               pageCount = versionRecord.PageCount,
                               distinctHostCount = hostnames.Count,
                               hostnames = hostnamesProjection,
                               languageMix,
                               boundaryIssuePct = versionRecord.BoundaryIssuePct,
                               suspect = versionRecord.Suspect,
                               suspectReasons = versionRecord.SuspectReasons,
                               boundaryHint = new { hint = boundaryHint, message = boundaryHintMessage }
                           };
        return JsonSerializer.Serialize(response, smJsonOptions);
    }

    private static (string? hint, string? message) ResolveBoundaryHint(double pct) => pct switch
    {
        >= BoundaryHintRecommendThreshold => (BoundaryHintRecommendedKey, BoundaryHintRecommendedMessage),
        >= BoundaryHintMayHelpThreshold => (BoundaryHintMayHelpKey, BoundaryHintMayHelpMessage),
        _ => (null, null)
    };

    [McpServerTool(Name = "get_dashboard_index")]
    [Description("Start here in any fresh or disoriented session. Returns a single-call " +
                 "DocRAG status overview: library/version counts, recent scrape jobs (with " +
                 "Stale flags for Running jobs that haven't progressed in 4+ hours), and up to " +
                 "20 suspect libraries. The SuggestedNextAction field always contains the highest-priority " +
                 "tool to call next (scrape_docs for empty DB, submit_url_correction for suspect libraries, " +
                 "cancel_scrape for stale-running jobs, null when healthy). Act on SuggestedNextAction " +
                 "before doing anything else."
                )]
    public static async Task<string> GetDashboardIndex(RepositoryFactory repositoryFactory,
                                                       [Description("Optional database profile name")]
                                                       string? profile = null,
                                                       CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);

        var libraryRepo = repositoryFactory.GetLibraryRepository(profile);
        var jobRepo = repositoryFactory.GetScrapeJobRepository(profile);

        var libraries = await libraryRepo.GetAllLibrariesAsync(ct);
        var recentJobs = await jobRepo.ListRecentAsync(limit: RecentJobsLimit, ct);

        var suspectList = new List<object>();
        int versionCount = 0;
        foreach (var lib in libraries)
        {
            foreach (var v in lib.AllVersions)
            {
                versionCount++;
                var versionRecord = await libraryRepo.GetVersionAsync(lib.Id, v, ct);
                if (versionRecord != null && versionRecord.Suspect && suspectList.Count < SuspectListCap)
                    suspectList.Add(new { library = lib.Id, version = v, reasons = versionRecord.SuspectReasons });
            }
        }

        var staleThreshold = TimeSpan.FromHours(StaleRunningThresholdHours);
        var recentJobsProjection = recentJobs.Select(j => new
                                                              {
                                                                  j.Id,
                                                                  j.Status,
                                                                  Library = j.Job.LibraryId,
                                                                  j.Job.Version,
                                                                  stale = j.Status == ScrapeJobStatus.Running
                                                                          && j.LastProgressAt.HasValue
                                                                          && DateTime.UtcNow - j.LastProgressAt.Value > staleThreshold,
                                                                  j.LastProgressAt
                                                              })
                                              .ToList();

        int staleRunning = recentJobs.Count(j => j.Status == ScrapeJobStatus.Running
                                                  && j.LastProgressAt.HasValue
                                                  && DateTime.UtcNow - j.LastProgressAt.Value > staleThreshold);

        object suggested = (libraries.Count == 0, suspectList.Count > 0, staleRunning > 0) switch
        {
            (true, _, _) => new { tool = (string?) SuggestToolScrape, message = EmptyDbSuggestion },
            (_, true, _) => new { tool = (string?) SuggestToolCorrectUrl, message = $"{suspectList.Count} suspect libraries — review and correct URLs." },
            (_, _, true) => new { tool = (string?) SuggestToolCancelScrape, message = $"{staleRunning} jobs have not progressed in over {StaleRunningThresholdHours}h." },
            _ => new { tool = (string?) null, message = SuggestMessageHealthy }
        };

        var response = new
                           {
                               libraryCount = libraries.Count,
                               versionCount,
                               recentJobs = recentJobsProjection,
                               suspectCount = suspectList.Count,
                               suspectLibraries = suspectList,
                               suggestedNextAction = suggested
                           };
        return JsonSerializer.Serialize(response, smJsonOptions);
    }

    private static readonly JsonSerializerOptions smJsonOptions = new() { WriteIndented = true };

    private const int MaxHostnamesReturned = 20;
    private const double BoundaryHintMayHelpThreshold = 5.0;
    private const double BoundaryHintRecommendThreshold = 10.0;
    private const string BoundaryHintRecommendedKey = "rechunk_recommended";
    private const string BoundaryHintRecommendedMessage = "rechunk_library recommended";
    private const string BoundaryHintMayHelpKey = "rechunk_may_help";
    private const string BoundaryHintMayHelpMessage = "rechunk_library may help";
    private const int RecentJobsLimit = 5;
    private const int SuspectListCap = 20;
    private const int StaleRunningThresholdHours = 4;
    private const string EmptyDbSuggestion = "Database is empty. Ingest a library to begin.";
    private const string SuggestToolScrape = "scrape_docs";
    private const string SuggestToolCorrectUrl = "submit_url_correction";
    private const string SuggestToolCancelScrape = "cancel_scrape";
    private const string SuggestMessageHealthy = "All libraries look healthy.";
}
