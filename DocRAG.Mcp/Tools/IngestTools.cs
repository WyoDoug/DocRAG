// // IngestTools.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using DocRAG.Core.Enums;
using DocRAG.Core.Models;
using DocRAG.Database.Repositories;
using ModelContextProtocol.Server;

#endregion

namespace DocRAG.Mcp.Tools;

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
    [Description("Single ingestion entrypoint. Inspects what we already know about " +
                 "(library, version) at the given URL and returns one of: " +
                 "RECON_NEEDED, READY_TO_SCRAPE, PARTIAL, STALE, VERSION_DRIFT, READY. " +
                 "The response includes the next tool to call and the parameters to pass " +
                 "so the calling LLM can follow the breadcrumb without remembering the " +
                 "full ingestion workflow."
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

        var libraryProfile = await profileRepo.GetAsync(library, version, ct);
        var chunkCount = await chunkRepo.GetChunkCountAsync(library, version, ct);

        bool stale = false;
        if (chunkCount > 0)
            stale = await chunkRepo.HasStaleChunksAsync(library, version, ParserVersionInfo.Current, ct);

        var response = ResolveStatus(libraryProfile, chunkCount, stale, library, version, url, force);
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

    private static IngestStatusResponse MakeReconNeeded(string library, string version, string url) =>
        new()
            {
                Status = IngestStatus.ReconNeeded,
                LibraryId = library,
                Version = version,
                Url = url,
                NextTool = "recon_library",
                Message = "No library profile cached. Have the calling LLM browse the docs site, "
                        + "identify languages/casing/likely symbols, and call submit_library_profile.",
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
                NextTool = "scrape_library",
                Message = message,
                NextToolArgs = new Dictionary<string, string>
                                   {
                                       ["rootUrl"] = url,
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
                Message = "Profile cached, index built, current parser version. Caller can query."
            };

    private static readonly JsonSerializerOptions smJsonOptions = new() { WriteIndented = true };

    private const string MessageReadyToScrapeFresh =
        "Profile cached, no chunks indexed. Call scrape_library to begin ingestion.";

    private const string MessageReadyToScrapeForce =
        "force=true: index exists but caller requested re-ingest. Call scrape_library to refresh.";
}
