// IngestTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using ModelContextProtocol.Server;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tool exposing the start_ingest state machine — the single front
///     door for ingesting or refreshing a documentation library. Inspects
///     what we already know about (library, version) at a URL and tells the
///     calling LLM what to do next: run reconnaissance, scrape, continue an
///     interrupted scrape, rescrub stale chunks, or query if everything is
///     current.
/// </summary>
[McpServerToolType]
public static class IngestTools
{
    [McpServerTool(Name = "start_ingest")]
    [Description("Single ingestion entrypoint — call this first when you want to ingest or refresh a library. " +
                 "Inspects (library, version) state and returns one of six states: " +
                 "IN_PROGRESS (scrape already running — poll get_scrape_status or call cancel_scrape), " +
                 "URL_SUSPECT (indexed content looks wrong — browse URL and call submit_url_correction), " +
                 "RECON_NEEDED (no profile — call recon_library then submit_library_profile), " +
                 "READY_TO_SCRAPE (profile cached, no chunks — call scrape_docs), " +
                 "STALE (chunks exist but parser is outdated — call rescrub_library), " +
                 "READY (fully indexed and current — call search_docs or get_class_reference). " +
                 "Each response includes NextTool and NextToolArgs so you can follow the breadcrumb without remembering the workflow."
                )]
    public static async Task<string> StartIngest(RepositoryFactory repositoryFactory,
                                                 [Description("Root URL of the docs site to ingest")]
                                                 string url,
                                                 [Description("Library identifier (e.g. 'aerotech-aeroscript'). Required for now; URL-based inference is a future enhancement.")]
                                                 string library,
                                                 [Description("Library version (e.g. '2025.3'). Required for now.")]
                                                 string version,
                                                 [Description("If true, auto-proceed through ready transitions (placeholder; not used yet).")]
                                                 bool auto = false,
                                                 [Description("If true, force re-ingest even when status would be READY (placeholder; not used yet).")]
                                                 bool force = false,
                                                 [Description("Optional database profile name")]
                                                 string? profile = null,
                                                 CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var profileRepo = repositoryFactory.GetLibraryProfileRepository(profile);
        var chunkRepo = repositoryFactory.GetChunkRepository(profile);
        var scrapeJobRepo = repositoryFactory.GetScrapeJobRepository(profile);
        var libraryRepo = repositoryFactory.GetLibraryRepository(profile);

        var libraryProfile = await profileRepo.GetAsync(library, version, ct);
        var chunkCount = await chunkRepo.GetChunkCountAsync(library, version, ct);

        bool stale = false;
        if (chunkCount > 0)
            stale = await chunkRepo.HasStaleChunksAsync(library, version, ParserVersionInfo.Current, ct);

        var activeJob = await scrapeJobRepo.GetActiveJobAsync(library, version, ct);
        var versionRecord = await libraryRepo.GetVersionAsync(library, version, ct);

        bool isInProgress = activeJob != null;
        bool isSuspect = versionRecord != null && versionRecord.Suspect;
        string activeJobId = activeJob?.Id ?? string.Empty;
        IReadOnlyList<string> suspectReasons = versionRecord?.SuspectReasons ?? [];

        IngestStatusResponse? suspectResponse = isSuspect && !isInProgress
            ? await MakeUrlSuspectAsync(library, version, url, suspectReasons, chunkRepo, ct)
            : null;

        IngestStatusResponse response = isInProgress switch
        {
            true => MakeInProgress(library, version, url, activeJobId),
            false => suspectResponse ?? ResolveStatus(libraryProfile, chunkCount, stale, library, version, url, force)
        };

        var json = JsonSerializer.Serialize(response, smJsonOptions);
        return json;
    }

    private static IngestStatusResponse ResolveStatus(LibraryProfile? libraryProfile,
                                                      int chunkCount,
                                                      bool stale,
                                                      string library,
                                                      string version,
                                                      string url,
                                                      bool force)
    {
        bool hasProfile = libraryProfile != null;
        bool hasChunks = chunkCount > 0;

        var response = (hasProfile, hasChunks, stale, force) switch
        {
            (false, _, _, _) => MakeReconNeeded(library, version, url),
            (true, false, _, _) => MakeReadyToScrape(library, version, url, MessageReadyToScrapeFresh),
            (true, true, true, _) => MakeStale(library, version, url),
            (true, true, false, true) => MakeReadyToScrape(library, version, url, MessageReadyToScrapeForce),
            (true, true, false, false) => MakeReady(library, version, url)
        };

        return response;
    }

    private static IngestStatusResponse MakeInProgress(string library, string version, string url, string jobId) =>
        new()
            {
                Status = IngestStatus.InProgress,
                LibraryId = library,
                Version = version,
                Url = url,
                NextTool = "get_scrape_status",
                Message = $"Scrape job {jobId} is already running. Poll get_scrape_status, or call cancel_scrape to abort.",
                NextToolArgs = new Dictionary<string, string>
                                   {
                                       ["jobId"] = jobId
                                   }
            };

    private static IngestStatusResponse MakeReconNeeded(string library, string version, string url) =>
        new()
            {
                Status = IngestStatus.ReconNeeded,
                LibraryId = library,
                Version = version,
                Url = url,
                NextTool = "recon_library",
                Message = "No library profile cached. Call recon_library (args in NextToolArgs) to get the schema and instructions, "
                        + "then browse the docs site and call submit_library_profile with the resulting JSON.",
                NextToolArgs = new Dictionary<string, string>
                                   {
                                       ["url"] = url,
                                       ["library"] = library,
                                       ["version"] = version
                                   }
            };

    private static IngestStatusResponse MakeReadyToScrape(string library, string version, string url, string message) =>
        new()
            {
                Status = IngestStatus.ReadyToScrape,
                LibraryId = library,
                Version = version,
                Url = url,
                NextTool = "scrape_docs",
                Message = message,
                NextToolArgs = new Dictionary<string, string>
                                   {
                                       ["url"] = url,
                                       ["libraryId"] = library,
                                       ["version"] = version
                                   }
            };

    private static IngestStatusResponse MakeStale(string library, string version, string url) =>
        new()
            {
                Status = IngestStatus.Stale,
                LibraryId = library,
                Version = version,
                Url = url,
                NextTool = "rescrub_library",
                Message = "Chunks exist but were extracted with an older parser. "
                        + "Call rescrub_library to re-run the extractor over stored content (no re-crawl).",
                NextToolArgs = new Dictionary<string, string>
                                   {
                                       ["library"] = library,
                                       ["version"] = version
                                   }
            };

    private static IngestStatusResponse MakeReady(string library, string version, string url) =>
        new()
            {
                Status = IngestStatus.Ready,
                LibraryId = library,
                Version = version,
                Url = url,
                Message = "Profile cached, index built, current parser version. Ready to query — call search_docs for natural-language search, get_class_reference for a specific type, or get_library_overview for an introduction."
            };

    private static async Task<IngestStatusResponse> MakeUrlSuspectAsync(string library,
                                                                         string version,
                                                                         string url,
                                                                         IReadOnlyList<string> suspectReasons,
                                                                         SaddleRAG.Core.Interfaces.IChunkRepository chunkRepo,
                                                                         CancellationToken ct)
    {
        var sampleTitles = await chunkRepo.GetSampleTitlesAsync(library, version, UrlSuspectSampleTitleLimit, ct);
        var hostnameDist = await chunkRepo.GetHostnameDistributionAsync(library, version, ct);

        var sampleTitlesJoined = string.Join(SemicolonSeparator, sampleTitles.Take(UrlSuspectSampleTitlesShown));
        var hostnamesJoined = string.Join(CommaSeparator, hostnameDist.Keys.Take(UrlSuspectHostnamesShown));
        var reasonsJoined = string.Join(CommaSeparator, suspectReasons);

        var result = new IngestStatusResponse
                         {
                             Status = IngestStatus.UrlSuspect,
                             LibraryId = library,
                             Version = version,
                             Url = url,
                             NextTool = "submit_url_correction",
                             Message = $"Indexed content looks wrong: {reasonsJoined}. "
                                     + $"Sample titles: {sampleTitlesJoined}. "
                                     + $"Hostnames: {hostnamesJoined}. "
                                     + "Browse the URL and call submit_url_correction with a better one if needed. "
                                     + "The library and version are pre-filled in NextToolArgs; you must supply newUrl yourself after browsing.",
                             NextToolArgs = new Dictionary<string, string>
                                               {
                                                   ["library"] = library,
                                                   ["version"] = version
                                               }
                         };
        return result;
    }

    private static readonly JsonSerializerOptions smJsonOptions = new() { WriteIndented = true };

    private const string MessageReadyToScrapeFresh =
        "Profile cached, no chunks indexed. Call scrape_docs (args in NextToolArgs) to begin ingestion.";

    private const string MessageReadyToScrapeForce =
        "force=true: index exists but caller requested re-ingest. Call scrape_docs (args in NextToolArgs) to refresh.";

    private const int UrlSuspectSampleTitleLimit = 5;
    private const int UrlSuspectSampleTitlesShown = 3;
    private const int UrlSuspectHostnamesShown = 5;
    private const string SemicolonSeparator = "; ";
    private const string CommaSeparator = ", ";
}
