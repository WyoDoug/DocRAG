// RescrubTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using DocRAG.Core.Models;
using DocRAG.Database.Repositories;
using DocRAG.Ingestion.Recon;
using ModelContextProtocol.Server;

#endregion

namespace DocRAG.Mcp.Tools;

/// <summary>
///     MCP tool exposing rescrub_library — the "scrub what we know"
///     entrypoint. Re-runs the symbol extractor (and optionally the
///     classifier) over chunks already stored in MongoDB. No re-crawling
///     the source pages, no re-chunking, no re-embedding. Used when the
///     parser changes and existing chunks need their Symbols[] /
///     QualifiedName / ParserVersion refreshed without paying crawl cost.
/// </summary>
[McpServerToolType]
public static class RescrubTools
{
    [McpServerTool(Name = "rescrub_library")]
    [Description("Re-run the identifier-aware extractor over chunks already stored for " +
                 "(library, version). Does NOT re-crawl, re-chunk, or re-embed. Updates " +
                 "Symbols[], QualifiedName, ParserVersion (and Category if reclassification " +
                 "is enabled). Bootstraps or rebuilds library_indexes (CodeFenceSymbols + " +
                 "Manifest). Returns counts, a sample of per-chunk diffs, and a BoundaryHint " +
                 "field: null (healthy), 'rechunk_library may help' (5%–10% boundary issues), or " +
                 "'rechunk_library recommended' (≥10%). Act on the hint before calling search_docs. " +
                 "Idempotent and resumable. If no LibraryProfile exists yet, returns " +
                 "RECON_NEEDED — call recon_library and submit_library_profile first."
                )]
    public static async Task<string> RescrubLibrary(RepositoryFactory repositoryFactory,
                                                    RescrubService service,
                                                    [Description("Library identifier (e.g. 'aerotech-aeroscript')")]
                                                    string library,
                                                    [Description("Library version (e.g. '2025.3')")]
                                                    string version,
                                                    [Description("If true, reports what would change without writing to MongoDB.")]
                                                    bool dryRun = false,
                                                    [Description("Force reclassification even when auto-detect would skip it. Omit to auto-decide from manifest history.")]
                                                    bool? reclassify = null,
                                                    [Description("Skip the pre-flight chunk-boundary audit (typically only used for tests).")]
                                                    bool skipBoundaryAudit = false,
                                                    [Description("If true (default), rebuild CodeFenceSymbols and Manifest.")]
                                                    bool rebuildIndexes = true,
                                                    [Description("Optional cap for spot-checking large libraries.")]
                                                    int? maxChunks = null,
                                                    [Description("Optional database profile name")]
                                                    string? profile = null,
                                                    CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var options = new RescrubOptions
                          {
                              DryRun = dryRun,
                              ReClassify = reclassify,
                              BoundaryAudit = !skipBoundaryAudit,
                              RebuildIndexes = rebuildIndexes,
                              MaxChunks = maxChunks
                          };

        var chunkRepo = repositoryFactory.GetChunkRepository(profile);
        var profileRepo = repositoryFactory.GetLibraryProfileRepository(profile);
        var indexRepo = repositoryFactory.GetLibraryIndexRepository(profile);
        var bm25ShardRepo = repositoryFactory.GetBm25ShardRepository(profile);
        var excludedRepo = repositoryFactory.GetExcludedSymbolsRepository(profile);
        var libraryRepo = repositoryFactory.GetLibraryRepository(profile);

        var result = await service.RescrubAsync(chunkRepo,
                                                profileRepo,
                                                indexRepo,
                                                bm25ShardRepo,
                                                excludedRepo,
                                                libraryRepo,
                                                library,
                                                version,
                                                options,
                                                ct
                                               );
        double pct = result.Processed > 0 ? 100.0 * result.BoundaryIssues / result.Processed : 0.0;
        string? hint = ResolveBoundaryHint(pct);

        var responseWithHint = new
                                   {
                                       Result = result,
                                       BoundaryHint = new { pct, hint }
                                   };
        var json = JsonSerializer.Serialize(responseWithHint, smJsonOptions);
        return json;
    }

    private static string? ResolveBoundaryHint(double pct) => pct switch
    {
        >= BoundaryHintRecommendThreshold => BoundaryHintRecommend,
        >= BoundaryHintMayHelpThreshold => BoundaryHintMayHelp,
        _ => null
    };

    private static readonly JsonSerializerOptions smJsonOptions = new() { WriteIndented = true };

    private const double BoundaryHintMayHelpThreshold = 5.0;
    private const double BoundaryHintRecommendThreshold = 10.0;
    private const string BoundaryHintMayHelp = "rechunk_library may help";
    private const string BoundaryHintRecommend = "rechunk_library recommended";
}
