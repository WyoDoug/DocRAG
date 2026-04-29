# Streaming Ingestion Pipeline Design

**Date:** 2026-04-13
**Status:** Draft
**Scope:** Refactor IngestionOrchestrator to a channel-based streaming pipeline with crawl resume support

## Overview

Replace the current sequential batch pipeline (crawl all → classify all → chunk all → embed all → index all) with a streaming architecture where pages flow through stages as they arrive. Each stage is connected by a `System.Threading.Channels.Channel<T>` queue with a single async consumer. Pages become searchable within seconds of being crawled instead of waiting for the entire crawl to complete.

Additionally: raise default MaxPages limits, add crawl resume/continuation support, and remove the heuristic classifier in favor of LLM-only classification.

## Pipeline Architecture

Five stages, each with a single consumer task, connected by bounded channels:

```
┌───────┐  Channel<PageRecord>  ┌──────────┐  Channel<PageRecord>  ┌───────┐  Channel<DocChunk[]>  ┌───────┐  Channel<DocChunk[]>  ┌───────┐
│ Crawl ├──────────────────────►│ Classify ├──────────────────────►│ Chunk ├──────────────────────►│ Embed ├──────────────────────►│ Index │
└───────┘                       └──────────┘                       └───────┘                       └───────┘                       └───────┘
```

### Channel Configuration

- Page channels (`Channel<PageRecord>`): bounded capacity ~50. Provides backpressure — if embedding is slow, the pipeline naturally throttles crawling.
- Chunk channels (`Channel<DocChunk[]>`): bounded capacity ~20. Each item is the chunk array for one page.
- All channels use `BoundedChannelFullMode.Wait` — writers block when the channel is full.

### Orchestration

`IngestionOrchestrator.IngestAsync` becomes a pipeline launcher:

1. Create four bounded channels.
2. Build the resume URL set (if resuming).
3. Start five consumer tasks (one per stage).
4. `await Task.WhenAll(crawlTask, classifyTask, chunkTask, embedTask, indexTask)`.
5. Final vector index rebuild (if any un-rebuilt chunks remain).
6. Update library metadata.

Each stage reads from its input channel until completion, then calls `Complete()` on its output channel. Completion cascades: crawl finishes → classify drains → chunk drains → embed drains → index drains → pipeline done.

### Cancellation

A shared `CancellationTokenSource` is passed to all stages. Cancelling it (user abort, fatal error) causes the crawl stage to stop producing, which cascades completion through the pipeline. Non-fatal errors in downstream stages do not cancel the token.

## Stage Details

### Stage 1: Crawl

Mostly unchanged from current `PageCrawler.CrawlAsync`. Two modifications:

- **Output to channel:** After upserting the PageRecord to MongoDB, write it to `ChannelWriter<PageRecord>`. This is how pages enter the pipeline.
- **Resume support:** Accept an `IReadOnlySet<string>? resumeUrls` parameter. If non-null, seed the BFS `visited` HashSet with those URLs before starting. Already-visited URLs are skipped without fetching.

API change:

```csharp
// Before
public async Task CrawlAsync(ScrapeJob job, Action<int>? onPageFetched, CancellationToken ct)

// After
public async Task CrawlAsync(ScrapeJob job, ChannelWriter<PageRecord> output, IReadOnlySet<string>? resumeUrls, Action<int>? onPageFetched, CancellationToken ct)
```

When crawling finishes (exhausted queue, hit MaxPages, or cancelled), calls `output.Complete()`.

The orchestrator builds the resume set by querying `IPageRepository.GetPagesAsync(libraryId, version)` and extracting URLs. PageCrawler does not depend on repositories — it receives a pre-built set.

### Stage 2: LLM Classification

Single consumer reads PageRecords from the crawl channel. For each page:

1. Call `LlmClassifier.ClassifyAsync(page, job.LibraryHint)`.
2. Update the page's `Category` in MongoDB via `IPageRepository.UpsertPageAsync`.
3. Write the classified PageRecord to the next channel.

If the LLM returns `Unclassified` or errors, the page flows downstream with `Unclassified` category. It does not get stuck. Non-fatal errors are logged and counted.

### Stage 3: Chunk

Single consumer reads classified PageRecords. For each page:

1. Call `CategoryAwareChunker.Chunk(page)` → `IReadOnlyList<DocChunk>`.
2. Write the `DocChunk[]` array to the next channel.

No database writes — chunks are not persisted until they have embeddings (Stage 4).

### Stage 4: Embed

Single consumer reads `DocChunk[]` arrays from the chunk channel. Accumulates chunks until it has a batch of 32 (or the input channel completes). For each batch:

1. Build embedding text: `"[{category}] [{libraryId}] [{pageTitle}]\n{content}"`, truncated to 6000 chars.
2. Call `IEmbeddingProvider.EmbedAsync(texts)`.
3. Attach embeddings to chunks.
4. Persist to MongoDB via `IChunkRepository.InsertChunksAsync` — chunks are written **once**, with embeddings already attached.
5. Write the embedded batch to the index channel.

If the embedding API fails for a batch, retry once, then skip and log. Skipped chunks do not reach the index.

### Stage 5: Index

Single consumer reads embedded `DocChunk[]` batches. Accumulates them, and every 100 chunks calls `IVectorSearchProvider.IndexChunksAsync` to rebuild the in-memory vector index. When the input channel completes, does a final rebuild with any remaining chunks.

This makes results searchable incrementally — not instantaneously per-page, but every ~100 chunks indexed.

**PagesCompleted tracking:** Each `DocChunk[]` array flowing through the pipeline carries the source `PageUrl`. The index stage maintains a set of page URLs whose chunks have all been indexed. When the last chunk batch for a page is indexed, `PagesCompleted` is incremented. (The chunk stage knows how many chunks a page produced; this count can be tagged on the array or tracked via a dictionary keyed by page URL.)

## Crawl Resume / Continuation

### Problem

Current crawl uses an in-memory `visited` HashSet and `Queue<CrawlEntry>`. Hitting MaxPages or any interruption means starting from scratch — no state is persisted.

### Solution: Auto-Detect + Explicit Tool

**Auto-detect on re-scrape:** When `IngestAsync` is called for a library+version that already has pages in the DB:

1. Query `IPageRepository.GetPagesAsync(libraryId, version)` to get all already-indexed URLs.
2. Pass these as the `resumeUrls` set to `PageCrawler.CrawlAsync`.
3. The BFS queue starts from `rootUrl` as always, but already-visited URLs are skipped before fetching.
4. The crawler re-discovers the link graph from root (cheap — visited pages skip instantly) and picks up new/undiscovered pages.

**Explicit `continue_scrape` MCP tool:** Takes `libraryId` + `version`. Retrieves the original `ScrapeJob` configuration (AllowedUrlPatterns, ExcludedUrlPatterns, etc.) from the most recent `ScrapeJobRecord` for that library+version in MongoDB. Calls the same `IngestAsync` path. The auto-detect resume logic means it works the same way — already-crawled URLs are skipped.

### Cleanup Strategy

- **Fresh scrape** (no existing pages in DB): No cleanup needed.
- **Force re-scrape** (`force=true`): Delete existing chunks for this library+version at pipeline start, before stages begin. Pages are upserted (overwritten) naturally.
- **Resume** (existing pages, not forced): No deletion. New chunks accumulate alongside existing ones. Chunk IDs are deterministic (`{libraryId}/{version}/{urlHash}/{index}`), so re-processing the same page produces the same chunk IDs — upsert semantics prevent duplicates. Note: the embed stage's chunk write must use upsert (not `InsertMany`) to support this. Change `IChunkRepository` to expose an `UpsertChunksAsync` method.

## Progress Reporting

### Counters on ScrapeJobRecord

Since stages run concurrently, `CurrentPhase` is replaced with individual counters:

| Field | Type | Description |
|---|---|---|
| `PipelineState` | string | `Running`, `Completed`, `Failed`, `Cancelled` |
| `PagesQueued` | int | URLs discovered and waiting in crawl BFS queue |
| `PagesFetched` | int | Pages downloaded from web |
| `PagesClassified` | int | Pages through LLM classification |
| `ChunksGenerated` | int | Chunks produced by chunking |
| `ChunksEmbedded` | int | Chunks with embeddings attached |
| `ChunksCompleted` | int | Chunks indexed and searchable |
| `PagesCompleted` | int | Pages fully indexed (all their chunks searchable) |
| `ErrorCount` | int | Non-fatal skips across all stages |

These fields are updated incrementally as each stage processes items, and persisted via `IScrapeJobRepository.UpsertAsync`.

The existing `get_scrape_status` MCP tool returns `ScrapeJobRecord` fields, so all counters are automatically queryable by the LLM. Example response: "1,200 URLs queued, 230 crawled, 180 classified, 85 pages searchable so far, 2 errors."

### Progress Callback

The `onProgress` callback signature changes to report all counters so CLI and MCP status endpoints can show a live view:

```
Queued: 1200 | Crawled: 230 | Classified: 180 | Chunks: 420 | Searchable: 310 chunks (85 pages)
```

## Error Handling

### Non-Fatal Errors (Per-Page/Per-Batch)

Individual failures do not kill the pipeline. Each stage handles its own errors:

- **Crawl:** Fetch errors logged, page skipped (existing behavior).
- **Classify:** LLM failure → page passes through as `Unclassified`, error logged.
- **Chunk:** Malformed content → page skipped, error logged.
- **Embed:** Embedding API failure → retry once, then skip batch, error logged.
- **Index:** Vector index rebuild failure → logged, next rebuild will include accumulated chunks.

All non-fatal errors increment `ErrorCount` on the `ScrapeJobRecord`.

### Fatal Errors

Embedding provider completely down, MongoDB unreachable, or cancellation token fired. These cancel the shared `CancellationTokenSource`, which cascades pipeline shutdown. The `ScrapeJobRecord` gets `Status = Failed` with the error message. All counters reflect how far the pipeline got, so a resume picks up from there.

## MaxPages Default Changes

| Location | Current | New |
|---|---|---|
| `ScrapeJobFactory.DefaultMaxPages` | 500 | 2500 |
| `ScrapeDocsTools` (scrape_docs MCP) | 500 | 2500 |
| `IngestionTools` (scrape_library MCP) | 5000 | 5000 (unchanged) |
| `ScrapeJob.MaxPages` model default | 5000 | 5000 (unchanged) |

Streaming removes the memory pressure that justified low limits — pages are indexed and discarded rather than accumulated.

## Deletions

- **`HeuristicClassifier.cs`** — removed entirely. LLM is the sole classification path.
- **`HeuristicClassifier` DI registrations** — removed from MCP `Program.cs` and CLI `Program.cs`.
- **Heuristic classification phase** in IngestionOrchestrator — replaced by LLM classify stage.
- **"Fetch all pages then classify" pattern** — replaced by channel flow.
- **Double chunk write** (insert without embeddings, delete, reinsert with embeddings) — chunks are written once with embeddings.

## Files Modified

| File | Change |
|---|---|
| `SaddleRAG.Ingestion/IngestionOrchestrator.cs` | Rewrite IngestAsync to channel-based pipeline |
| `SaddleRAG.Ingestion/Crawling/PageCrawler.cs` | Add ChannelWriter output, resumeUrls parameter, PagesQueued reporting |
| `SaddleRAG.Core/Models/ScrapeJobRecord.cs` | Add new counter fields, replace CurrentPhase with PipelineState |
| `SaddleRAG.Core/Models/ScrapeJob.cs` | No structural change (MaxPages default unchanged at model level) |
| `SaddleRAG.Ingestion/Scanning/ScrapeJobFactory.cs` | DefaultMaxPages 500 → 2500 |
| `SaddleRAG.Ingestion/ScrapeJobRunner.cs` | Update progress callback handling for new counters |
| `SaddleRAG.Mcp/Tools/ScrapeDocsTools.cs` | MaxPages default 500 → 2500, add continue_scrape tool |
| `SaddleRAG.Mcp/Tools/IngestionTools.cs` | Update status reporting for new counters |
| `SaddleRAG.Mcp/Program.cs` | Remove HeuristicClassifier registration |
| `SaddleRAG.Cli/Program.cs` | Remove HeuristicClassifier registration, update progress display |
| `SaddleRAG.Ingestion/Classification/HeuristicClassifier.cs` | **Delete** |
| `SaddleRAG.Tests/Classification/HeuristicClassifierTests.cs` | **Delete** (if exists) |
| `SaddleRAG.Tests/Scanning/ScrapeJobFactoryTests.cs` | Update DefaultMaxPages assertion |
