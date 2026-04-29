// RechunkTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Recon;
using ModelContextProtocol.Server;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tool exposing <c>rechunk_library</c> — refresh an already-ingested
///     library against the current chunker code, without re-crawling. Use this
///     after a chunker change ships and existing libraries should benefit
///     without paying the cost of a full re-ingest.
/// </summary>
[McpServerToolType]
public static class RechunkTools
{
    [McpServerTool(Name = "rechunk_library")]
    [Description("Re-run the chunker over pages already stored for (library, version). " +
                 "Replaces all chunks and re-embeds, then requires rescrub_library as a mandatory follow-up " +
                 "to populate corpus-aware Symbols[] and rebuild library_indexes — do not skip this. " +
                 "NO re-crawl, NO re-classify. Use after a chunker code change when existing chunks should " +
                 "be re-cut without re-fetching the docs site. Returns counts plus before/after BoundaryIssues " +
                 "so you can confirm the chunker change actually helped."
                )]
    public static async Task<string> RechunkLibrary(RepositoryFactory repositoryFactory,
                                                    RechunkService service,
                                                    [Description("Library identifier (e.g. 'aerotech-aeroscript')")]
                                                    string library,
                                                    [Description("Library version (e.g. '1.0')")]
                                                    string version,
                                                    [Description("If true, reports what would change (chunk counts, before/after BoundaryIssues) without writing to MongoDB or touching the vector index.")]
                                                    bool dryRun = false,
                                                    [Description("Skip the chunk-boundary audit. Default false; the audit is the primary signal that the new chunker code did its job.")]
                                                    bool skipBoundaryAudit = false,
                                                    [Description("Optional cap for spot-checking large libraries.")]
                                                    int? maxPages = null,
                                                    [Description("Optional database profile name (use list_profiles to discover)")]
                                                    string? profile = null,
                                                    CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var options = new RechunkOptions
                          {
                              DryRun = dryRun,
                              BoundaryAudit = !skipBoundaryAudit,
                              MaxPages = maxPages
                          };

        var pageRepo = repositoryFactory.GetPageRepository(profile);
        var chunkRepo = repositoryFactory.GetChunkRepository(profile);
        var profileRepo = repositoryFactory.GetLibraryProfileRepository(profile);

        var result = await service.RechunkAsync(profile, pageRepo, chunkRepo, profileRepo, library, version, options, ct);
        var json = JsonSerializer.Serialize(result, smJsonOptions);
        return json;
    }

    private static readonly JsonSerializerOptions smJsonOptions = new() { WriteIndented = true };
}
