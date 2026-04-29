// RechunkService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Chunking;
using SaddleRAG.Ingestion.Symbols;
using Microsoft.Extensions.Logging;

#endregion

namespace SaddleRAG.Ingestion.Recon;

/// <summary>
///     Workhorse for <c>rechunk_library</c>. Re-runs the chunker over pages
///     already stored in MongoDB without re-crawling. Used after a chunker
///     code change to refresh existing libraries — e.g., when the chunk-
///     boundary heuristic stops splitting dotted identifiers and existing
///     libraries should benefit without re-fetching the docs site.
///
///     What it does:
///       1. Load existing PageRecords for (libraryId, version)
///       2. Run CategoryAwareChunker over each page (with current chunker code)
///       3. Re-embed the new chunks via the configured IEmbeddingProvider
///       4. Replace existing chunks atomically (delete + UpsertChunksAsync)
///       5. Reload the in-memory vector index
///       6. (Optionally) audit chunk boundaries before/after
///
///     What it does NOT do:
///       - Re-crawl pages (that's <c>scrape_docs force=true</c>)
///       - Re-classify pages (that's the <c>reclassify</c> CLI command)
///       - Build the full corpus-aware Symbols[] / QualifiedName — the
///         freshly-chunked symbols are first-pass shape-only. Call
///         <c>rescrub_library</c> after rechunk to populate corpus-aware
///         Symbols and update the library_indexes (Bm25, CodeFenceSymbols,
///         Manifest).
/// </summary>
public class RechunkService
{
    public RechunkService(CategoryAwareChunker chunker,
                          IEmbeddingProvider embeddingProvider,
                          IVectorSearchProvider vectorSearch,
                          ILogger<RechunkService> logger)
    {
        mChunker = chunker;
        mEmbeddingProvider = embeddingProvider;
        mVectorSearch = vectorSearch;
        mLogger = logger;
    }

    private readonly CategoryAwareChunker mChunker;
    private readonly IEmbeddingProvider mEmbeddingProvider;
    private readonly IVectorSearchProvider mVectorSearch;
    private readonly ILogger<RechunkService> mLogger;

    /// <summary>
    ///     Re-chunk all pages for (libraryId, version). Idempotent. When
    ///     <see cref="RechunkOptions.DryRun"/> is true, no DB writes occur
    ///     and the vector index is not touched — the result still includes
    ///     the projected before/after boundary-issue counts.
    /// </summary>
    public async Task<RechunkResult> RechunkAsync(string? profile,
                                                  IPageRepository pageRepo,
                                                  IChunkRepository chunkRepo,
                                                  ILibraryProfileRepository profileRepo,
                                                  string libraryId,
                                                  string version,
                                                  RechunkOptions options,
                                                  CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pageRepo);
        ArgumentNullException.ThrowIfNull(chunkRepo);
        ArgumentNullException.ThrowIfNull(profileRepo);
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentNullException.ThrowIfNull(options);

        var libraryProfile = await profileRepo.GetAsync(libraryId, version, ct);
        var pages = await pageRepo.GetPagesAsync(libraryId, version, ct);
        var scopedPages = options.MaxPages.HasValue
                              ? pages.Take(options.MaxPages.Value).ToList()
                              : pages.ToList();

        var existingChunks = await chunkRepo.GetChunksAsync(libraryId, version, ct);
        var oldChunkCount = existingChunks.Count;
        var oldBoundaryIssues = options.BoundaryAudit ? ChunkBoundaryAudit.CountIssues(existingChunks) : 0;

        mLogger.LogInformation("Rechunk starting for {LibraryId} v{Version}: {Pages} pages, {OldChunks} existing chunks",
                               libraryId,
                               version,
                               scopedPages.Count,
                               oldChunkCount
                              );

        var newChunks = scopedPages.SelectMany(p => mChunker.Chunk(p, libraryProfile)).ToList();
        var newIssueDetails = options.BoundaryAudit
                                  ? ChunkBoundaryAudit.EnumerateIssues(newChunks).Take(BoundaryIssueSampleLimit + 1).ToList()
                                  : [];
        var newBoundaryIssues = options.BoundaryAudit ? ChunkBoundaryAudit.CountIssues(newChunks) : 0;
        var sampleIssues = newIssueDetails.Take(BoundaryIssueSampleLimit).ToList();

        var embeddedCount = 0;
        var message = options.DryRun ? DryRunMessage : AppliedMessage;

        if (!options.DryRun)
        {
            var embeddedChunks = await EmbedAllAsync(newChunks, ct);
            await chunkRepo.DeleteChunksAsync(libraryId, version, ct);
            await chunkRepo.UpsertChunksAsync(embeddedChunks, ct);
            embeddedCount = embeddedChunks.Count;

            try
            {
                await mVectorSearch.IndexChunksAsync(profile, libraryId, version, embeddedChunks, ct);
                mLogger.LogInformation("Reloaded vector index for {Profile}/{LibraryId} v{Version}: {Count} chunks",
                                       profile ?? "(default)",
                                       libraryId,
                                       version,
                                       embeddedChunks.Count
                                      );
            }
            catch(Exception ex)
            {
                mLogger.LogWarning(ex, "Vector index reload failed after rechunk; chunks are persisted but search may be stale until next reload_profile");
            }
        }

        var result = new RechunkResult
                         {
                             LibraryId = libraryId,
                             Version = version,
                             PagesProcessed = scopedPages.Count,
                             OldChunkCount = oldChunkCount,
                             NewChunkCount = newChunks.Count,
                             BoundaryIssuesBefore = oldBoundaryIssues,
                             BoundaryIssuesAfter = newBoundaryIssues,
                             ChunksEmbedded = embeddedCount,
                             DryRun = options.DryRun,
                             Message = message,
                             BoundaryIssueSamples = sampleIssues
                         };
        return result;
    }

    private async Task<IReadOnlyList<DocChunk>> EmbedAllAsync(IReadOnlyList<DocChunk> chunks, CancellationToken ct)
    {
        var output = new List<DocChunk>(chunks.Count);
        for(var i = 0; i < chunks.Count; i += EmbedBatchSize)
        {
            var batch = chunks.Skip(i).Take(EmbedBatchSize).ToList();
            var texts = batch.Select(c => TruncateForEmbedding(BuildEmbedText(c), MaxEmbedChars)).ToList();
            var embeddings = await EmbedWithRetryAsync(texts, ct);
            for(var j = 0; j < batch.Count; j++)
                output.Add(batch[j] with { Embedding = embeddings[j] });

            mLogger.LogDebug("Rechunk embedded batch {Start}-{End} of {Total}", i, i + batch.Count, chunks.Count);
        }

        return output;
    }

    private async Task<float[][]> EmbedWithRetryAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        float[][] result;
        try
        {
            result = await mEmbeddingProvider.EmbedAsync(texts, ct);
        }
        catch(Exception ex)
        {
            mLogger.LogWarning(ex, "Embedding failed during rechunk, retrying once");
            result = await mEmbeddingProvider.EmbedAsync(texts, ct);
        }
        return result;
    }

    private static string BuildEmbedText(DocChunk chunk) =>
        $"[{chunk.Category}] [{chunk.LibraryId}] [{chunk.PageTitle}]\n{chunk.Content}";

    private static string TruncateForEmbedding(string text, int maxChars) =>
        text.Length > maxChars ? text[..maxChars] : text;

    private const int EmbedBatchSize = 32;
    private const int MaxEmbedChars = 6000;
    private const int BoundaryIssueSampleLimit = 10;

    private const string DryRunMessage =
        "Dry run — no chunks written, no embeddings generated. Re-run without dryRun to apply.";

    private const string AppliedMessage =
        "Rechunk complete. Call rescrub_library to populate corpus-aware Symbols[] and rebuild library_indexes.";
}
