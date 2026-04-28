// UrlCorrectionTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using DocRAG.Core.Models;
using DocRAG.Database.Repositories;
using DocRAG.Ingestion;
using DocRAG.Ingestion.Scanning;
using ModelContextProtocol.Server;

#endregion

namespace DocRAG.Mcp.Tools;

/// <summary>
///     MCP tool for re-rooting a suspect scrape at a corrected URL.
///     Recon-style callback after start_ingest reports URL_SUSPECT:
///     drops the existing chunks/pages/profile/indexes/shards, clears
///     LibraryVersion.Suspect, then queues a fresh scrape_docs at the
///     corrected URL.
/// </summary>
[McpServerToolType]
public static class UrlCorrectionTools
{
    [McpServerTool(Name = "submit_url_correction")]
    [Description("Re-root a scrape at a corrected URL. Drops the existing chunks, " +
                 "pages, profile, indexes, and bm25 shards for (library, version), " +
                 "clears the Suspect flag, then queues a fresh scrape_docs at newUrl. " +
                 "Use when start_ingest returned URL_SUSPECT or when scrape_docs(resume=true) " +
                 "returned Status=Refused with Reason=URL_SUSPECT — both indicate the indexed " +
                 "content is probably wrong. Browse the URL yourself first to confirm a better one. " +
                 "dryRun=false is the default — the tool applies immediately. Pass dryRun=true to preview."
                )]
    public static async Task<string> SubmitUrlCorrection(RepositoryFactory repositoryFactory,
                                                         ScrapeJobRunner runner,
                                                         [Description("Library identifier")]
                                                         string library,
                                                         [Description("Version")]
                                                         string version,
                                                         [Description("Corrected docs root URL")]
                                                         string newUrl,
                                                         [Description("If true, preview without writing or queueing.")]
                                                         bool dryRun = false,
                                                         [Description("Optional database profile name")]
                                                         string? profile = null,
                                                         CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(newUrl);

        var libraryRepo = repositoryFactory.GetLibraryRepository(profile);
        var chunkRepo = repositoryFactory.GetChunkRepository(profile);
        var pageRepo = repositoryFactory.GetPageRepository(profile);
        var profileRepo = repositoryFactory.GetLibraryProfileRepository(profile);
        var indexRepo = repositoryFactory.GetLibraryIndexRepository(profile);
        var bm25Repo = repositoryFactory.GetBm25ShardRepository(profile);

        string result;
        if (dryRun)
        {
            var chunks = await chunkRepo.GetChunkCountAsync(library, version, ct);
            var pages = await pageRepo.GetPageCountAsync(library, version, ct);
            var preview = new
                              {
                                  DryRun = true,
                                  WouldDelete = new { Chunks = chunks, Pages = pages, Profiles = 1, Indexes = 1, Bm25Shards = 1 },
                                  WouldQueue = new { RootUrl = newUrl, Library = library, Version = version }
                              };
            result = JsonSerializer.Serialize(preview, smJsonOptions);
        }
        else
        {
            var chunks = await chunkRepo.DeleteChunksAsync(library, version, ct);
            var pages = await pageRepo.DeleteAsync(library, version, ct);
            await profileRepo.DeleteAsync(library, version, ct);
            await indexRepo.DeleteAsync(library, version, ct);
            await bm25Repo.DeleteAsync(library, version, ct);
            await libraryRepo.ClearSuspectAsync(library, version, ct);

            var job = ScrapeJobFactory.CreateFromUrl(newUrl,
                                                     library,
                                                     version,
                                                     hint: CorrectedHint,
                                                     maxPages: DefaultMaxPages,
                                                     fetchDelayMs: DefaultFetchDelayMs,
                                                     forceClean: true);
            var jobId = await runner.QueueAsync(job, profile, ct);

            var response = new
                               {
                                   DryRun = false,
                                   Cleared = new { Chunks = chunks, Pages = pages },
                                   JobId = jobId,
                                   Status = StatusQueued,
                                   Message = $"Suspect chunks dropped, scrape re-queued at {newUrl}. Poll get_scrape_status with jobId='{jobId}'."
                               };
            result = JsonSerializer.Serialize(response, smJsonOptions);
        }

        return result;
    }

    private static readonly JsonSerializerOptions smJsonOptions = new() { WriteIndented = true };

    private const string CorrectedHint = "(corrected URL)";
    private const string StatusQueued = "Queued";
    private const int DefaultMaxPages = 0;
    private const int DefaultFetchDelayMs = 500;
}
