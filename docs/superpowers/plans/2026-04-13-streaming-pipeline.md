# Streaming Ingestion Pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor IngestionOrchestrator from a sequential batch pipeline to a channel-based streaming pipeline where pages flow through classify→chunk→embed→index as they arrive from the crawler, becoming searchable in seconds instead of minutes.

**Architecture:** Five stages connected by bounded `System.Threading.Channels.Channel<T>` queues, each with a single async consumer task. The crawl stage writes PageRecords into the first channel; each downstream stage reads, processes, and writes to the next channel. Completion cascades when crawl finishes. Resume support seeds the crawler's visited set from already-indexed URLs in MongoDB.

**Tech Stack:** .NET 8, System.Threading.Channels, MongoDB, Ollama (LLM classification + embeddings)

**Spec:** `docs/superpowers/specs/2026-04-13-streaming-pipeline-design.md`

---

### Task 1: Update ScrapeJobRecord with streaming counters

Replace the single `CurrentPhase` string with per-stage counters so each pipeline stage can report progress independently.

**Files:**
- Modify: `SaddleRAG.Core/Models/ScrapeJobRecord.cs`

- [ ] **Step 1: Replace CurrentPhase with PipelineState and add new counter fields**

Replace the entire content of `ScrapeJobRecord.cs` with:

```csharp
// ScrapeJobRecord.cs
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.

using SaddleRAG.Core.Enums;

namespace SaddleRAG.Core.Models;

/// <summary>
///     Tracks the lifecycle of a single scrape job for status polling.
/// </summary>
public class ScrapeJobRecord
{
    /// <summary>
    ///     Unique job identifier (GUID string).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     The original scrape job configuration that was submitted.
    /// </summary>
    public required ScrapeJob Job { get; init; }

    /// <summary>
    ///     Database profile this job is writing to.
    /// </summary>
    public string? Profile { get; init; }

    /// <summary>
    ///     Current status.
    /// </summary>
    public ScrapeJobStatus Status { get; set; } = ScrapeJobStatus.Queued;

    /// <summary>
    ///     Pipeline state: Running, Completed, Failed, Cancelled.
    /// </summary>
    public string PipelineState { get; set; } = "Queued";

    /// <summary>
    ///     URLs discovered and waiting in crawl BFS queue.
    /// </summary>
    public int PagesQueued { get; set; }

    /// <summary>
    ///     Pages downloaded from web.
    /// </summary>
    public int PagesFetched { get; set; }

    /// <summary>
    ///     Pages through LLM classification.
    /// </summary>
    public int PagesClassified { get; set; }

    /// <summary>
    ///     Chunks produced by chunking.
    /// </summary>
    public int ChunksGenerated { get; set; }

    /// <summary>
    ///     Chunks with embeddings attached.
    /// </summary>
    public int ChunksEmbedded { get; set; }

    /// <summary>
    ///     Chunks indexed and searchable.
    /// </summary>
    public int ChunksCompleted { get; set; }

    /// <summary>
    ///     Pages fully indexed (all their chunks searchable).
    /// </summary>
    public int PagesCompleted { get; set; }

    /// <summary>
    ///     Non-fatal error count across all stages.
    /// </summary>
    public int ErrorCount { get; set; }

    /// <summary>
    ///     Error message if Status is Failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    ///     When the job was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    ///     When the job started running.
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    ///     When the job finished (success, failure, or cancellation).
    /// </summary>
    public DateTime? CompletedAt { get; set; }
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build E:\Projects\RAG\SaddleRAG.Core\SaddleRAG.Core.csproj`
Expected: Build errors in files that reference `CurrentPhase` — that's expected, we'll fix them in later tasks.

- [ ] **Step 3: Commit**

Commit message: `refactor: replace ScrapeJobRecord.CurrentPhase with per-stage streaming counters`

---

### Task 2: Add UpsertChunksAsync to IChunkRepository and ChunkRepository

The streaming embed stage writes chunks with embeddings in one pass (no delete+reinsert). For resume support, it must upsert rather than insert to handle duplicate chunk IDs.

**Files:**
- Modify: `SaddleRAG.Core/Interfaces/IChunkRepository.cs`
- Modify: `SaddleRAG.Database/Repositories/ChunkRepository.cs`

- [ ] **Step 1: Add UpsertChunksAsync to IChunkRepository**

Add this method to the `IChunkRepository` interface, after the existing `InsertChunksAsync` method:

```csharp
    /// <summary>
    ///     Upsert a batch of chunks (insert or replace by Id).
    ///     Used by the streaming embed stage to support resume without duplicates.
    /// </summary>
    Task UpsertChunksAsync(IReadOnlyList<DocChunk> chunks, CancellationToken ct = default);
```

- [ ] **Step 2: Implement UpsertChunksAsync in ChunkRepository**

Add this method to `ChunkRepository`, after the existing `InsertChunksAsync` method:

```csharp
    /// <inheritdoc />
    public async Task UpsertChunksAsync(IReadOnlyList<DocChunk> chunks, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        if (chunks.Count == 0)
            return;

        var bulkOps = chunks.Select(chunk =>
        {
            var filter = Builders<DocChunk>.Filter.Eq(c => c.Id, chunk.Id);
            return new ReplaceOneModel<DocChunk>(filter, chunk) { IsUpsert = true };
        }).ToList();

        await mContext.Chunks.BulkWriteAsync(bulkOps, cancellationToken: ct);
    }
```

- [ ] **Step 3: Build to verify compilation**

Run: `dotnet build E:\Projects\RAG\SaddleRAG.Database\SaddleRAG.Database.csproj`
Expected: PASS

- [ ] **Step 4: Commit**

Commit message: `feat: add UpsertChunksAsync to IChunkRepository for streaming resume support`

---

### Task 3: Update MaxPages defaults

Raise limits now that streaming removes the memory pressure.

**Files:**
- Modify: `SaddleRAG.Ingestion/Scanning/ScrapeJobFactory.cs`
- Modify: `SaddleRAG.Mcp/Tools/ScrapeDocsTools.cs`
- Modify: `SaddleRAG.Tests/Scanning/ScrapeJobFactoryTests.cs`

- [ ] **Step 1: Update ScrapeJobFactory.DefaultMaxPages from 500 to 2500**

In `ScrapeJobFactory.cs`, change:

```csharp
    private const int DefaultMaxPages = 500;
```

to:

```csharp
    private const int DefaultMaxPages = 2500;
```

- [ ] **Step 2: Update ScrapeDocsTools maxPages default from 500 to 2500**

In `ScrapeDocsTools.cs`, change the `ScrapeDocs` method parameter:

```csharp
        [Description("Maximum pages to crawl (default 500)")] int maxPages = 500,
```

to:

```csharp
        [Description("Maximum pages to crawl (default 2500)")] int maxPages = 2500,
```

- [ ] **Step 3: Update the test constant**

In `ScrapeJobFactoryTests.cs`, change:

```csharp
    private const int DefaultMaxPages = 500;
```

to:

```csharp
    private const int DefaultMaxPages = 2500;
```

- [ ] **Step 4: Run tests to verify**

Run: `dotnet test E:\Projects\RAG\SaddleRAG.Tests\SaddleRAG.Tests.csproj --filter ScrapeJobFactoryTests`
Expected: All 13 tests pass.

- [ ] **Step 5: Commit**

Commit message: `feat: raise default MaxPages from 500 to 2500 for streaming pipeline`

---

### Task 4: Delete HeuristicClassifier and remove registrations

LLM is the sole classification path.

**Files:**
- Delete: `SaddleRAG.Ingestion/Classification/HeuristicClassifier.cs`
- Delete: `SaddleRAG.Tests/Classification/HeuristicClassifierTests.cs`
- Modify: `SaddleRAG.Mcp/Program.cs`
- Modify: `SaddleRAG.Cli/Program.cs`

- [ ] **Step 1: Delete HeuristicClassifier.cs**

Run: `rm E:\Projects\RAG\SaddleRAG.Ingestion\Classification\HeuristicClassifier.cs`

- [ ] **Step 2: Delete HeuristicClassifierTests.cs**

Run: `rm E:\Projects\RAG\SaddleRAG.Tests\Classification\HeuristicClassifierTests.cs`

- [ ] **Step 3: Remove HeuristicClassifier registration from MCP Program.cs**

In `SaddleRAG.Mcp/Program.cs`, delete this line:

```csharp
builder.Services.AddSingleton<HeuristicClassifier>();
```

And remove the using if it becomes unused:

```csharp
using SaddleRAG.Ingestion.Classification;
```

Replace it with just:

```csharp
using SaddleRAG.Ingestion.Classification;
```

(Keep it — LlmClassifier still needs it.)

- [ ] **Step 4: Remove HeuristicClassifier registration from CLI Program.cs**

In `SaddleRAG.Cli/Program.cs`, delete this line:

```csharp
services.AddSingleton<HeuristicClassifier>();
```

And remove the `using SaddleRAG.Ingestion.Classification;` import if only HeuristicClassifier used it. (Keep it if LlmClassifier is also registered here.)

- [ ] **Step 5: Build the full solution to find any remaining references**

Run: `dotnet build E:\Projects\RAG\SaddleRAG.sln`
Expected: Build errors in `IngestionOrchestrator.cs` (references `HeuristicClassifier` and `CurrentPhase`) — those are expected and will be fixed in Task 5.

- [ ] **Step 6: Commit**

Commit message: `refactor: remove HeuristicClassifier in favor of LLM-only classification`

---

### Task 5: Modify PageCrawler for channel output and resume

Add `ChannelWriter<PageRecord>` output parameter and `IReadOnlySet<string>? resumeUrls` for seeding the visited set. Add `Action<int>? onQueued` callback for PagesQueued reporting.

**Files:**
- Modify: `SaddleRAG.Ingestion/Crawling/PageCrawler.cs`

- [ ] **Step 1: Add using for System.Threading.Channels**

Add at the top of `PageCrawler.cs`:

```csharp
using System.Threading.Channels;
```

- [ ] **Step 2: Change the CrawlAsync signature**

Replace:

```csharp
    public async Task CrawlAsync(
        ScrapeJob job,
        Action<int>? onPageFetched = null,
        CancellationToken ct = default)
```

with:

```csharp
    public async Task CrawlAsync(
        ScrapeJob job,
        ChannelWriter<PageRecord> output,
        IReadOnlySet<string>? resumeUrls = null,
        Action<int>? onPageFetched = null,
        Action<int>? onQueued = null,
        CancellationToken ct = default)
```

- [ ] **Step 3: Seed visited set from resumeUrls and write to channel**

In the `CrawlAsync` method body, after the line:

```csharp
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
```

Add:

```csharp
        if (resumeUrls != null)
        {
            foreach (var url in resumeUrls)
                visited.Add(url);
            mLogger.LogInformation("Resume: seeded visited set with {Count} existing URLs", resumeUrls.Count);
        }
```

- [ ] **Step 4: Add PagesQueued reporting to the crawl loop**

In the crawl loop, after the `onPageFetched` callback line:

```csharp
                if (pageCount > previousCount)
                    onPageFetched?.Invoke(pageCount);
```

Add:

```csharp
                onQueued?.Invoke(queue.Count);
```

- [ ] **Step 5: Write fetched pages to the output channel**

In `FetchCrawlPageAsync`, after the line:

```csharp
                await mPageRepository.UpsertPageAsync(pageRecord, ct);
                result = pageCount + 1;
```

Add:

```csharp
                await output.WriteAsync(pageRecord, ct);
```

- [ ] **Step 6: Complete the channel when crawling finishes**

At the end of `CrawlAsync`, after the `mLogger.LogInformation("Crawl complete...")` line, add:

```csharp
        output.Complete();
```

- [ ] **Step 7: Build to verify**

Run: `dotnet build E:\Projects\RAG\SaddleRAG.Ingestion\SaddleRAG.Ingestion.csproj`
Expected: Build errors in `IngestionOrchestrator.cs` due to changed `CrawlAsync` signature — expected, fixed in Task 6.

- [ ] **Step 8: Commit**

Commit message: `feat: add channel output, resume support, and queue reporting to PageCrawler`

---

### Task 6: Rewrite IngestionOrchestrator as streaming pipeline

This is the core task. Replace the sequential five-phase pipeline with channel-connected stages.

**Files:**
- Modify: `SaddleRAG.Ingestion/IngestionOrchestrator.cs`

- [ ] **Step 1: Replace the entire IngestionOrchestrator.cs**

```csharp
// IngestionOrchestrator.cs
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.

using System.Threading.Channels;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Chunking;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Crawling;
using Microsoft.Extensions.Logging;

namespace SaddleRAG.Ingestion;

/// <summary>
///     Orchestrates the streaming ingestion pipeline:
///     crawl → classify → chunk → embed → index.
///     Each stage runs as a single async consumer connected by bounded channels.
/// </summary>
public class IngestionOrchestrator
{
    private const int PageChannelCapacity = 50;
    private const int ChunkChannelCapacity = 20;
    private const int EmbedBatchSize = 32;
    private const int IndexRebuildInterval = 100;
    // Safety limit for embedding context window (nomic-embed-text: 2048 tokens)
    // Use 6000 chars as hard cap (~2000 tokens at ~3 chars/token)
    private const int MaxEmbedChars = 6000;

    private readonly PageCrawler mCrawler;
    private readonly LlmClassifier mLlmClassifier;
    private readonly CategoryAwareChunker mChunker;
    private readonly IEmbeddingProvider mEmbeddingProvider;
    private readonly IVectorSearchProvider mVectorSearch;
    private readonly ILibraryRepository mLibraryRepository;
    private readonly IPageRepository mPageRepository;
    private readonly IChunkRepository mChunkRepository;
    private readonly ILogger<IngestionOrchestrator> mLogger;

    public IngestionOrchestrator(
        PageCrawler crawler,
        LlmClassifier llmClassifier,
        CategoryAwareChunker chunker,
        IEmbeddingProvider embeddingProvider,
        IVectorSearchProvider vectorSearch,
        ILibraryRepository libraryRepository,
        IPageRepository pageRepository,
        IChunkRepository chunkRepository,
        ILogger<IngestionOrchestrator> logger)
    {
        mCrawler = crawler;
        mLlmClassifier = llmClassifier;
        mChunker = chunker;
        mEmbeddingProvider = embeddingProvider;
        mVectorSearch = vectorSearch;
        mLibraryRepository = libraryRepository;
        mPageRepository = pageRepository;
        mChunkRepository = chunkRepository;
        mLogger = logger;
    }

    /// <summary>
    ///     Run the streaming ingestion pipeline for a scrape job.
    /// </summary>
    public async Task IngestAsync(
        ScrapeJob job,
        string? profile = null,
        bool forceClean = false,
        Action<ScrapeJobRecord>? onProgress = null,
        ScrapeJobRecord? jobRecord = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        mLogger.LogInformation("Starting streaming ingestion for {LibraryId} v{Version}", job.LibraryId, job.Version);

        // Build resume URL set from existing pages in DB
        var existingPages = await mPageRepository.GetPagesAsync(job.LibraryId, job.Version, ct);
        IReadOnlySet<string>? resumeUrls = null;
        if (existingPages.Count > 0 && !forceClean)
        {
            resumeUrls = existingPages.Select(p => p.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
            mLogger.LogInformation("Resume mode: {Count} existing pages found", resumeUrls.Count);
        }

        // On force re-scrape, clear existing chunks before pipeline starts
        if (forceClean)
        {
            await mChunkRepository.DeleteChunksAsync(job.LibraryId, job.Version, ct);
            mLogger.LogInformation("Force clean: deleted existing chunks for {LibraryId} v{Version}",
                job.LibraryId, job.Version);
        }

        // Create bounded channels
        var crawlToClassify = Channel.CreateBounded<PageRecord>(
            new BoundedChannelOptions(PageChannelCapacity) { FullMode = BoundedChannelFullMode.Wait });
        var classifyToChunk = Channel.CreateBounded<PageRecord>(
            new BoundedChannelOptions(PageChannelCapacity) { FullMode = BoundedChannelFullMode.Wait });
        var chunkToEmbed = Channel.CreateBounded<DocChunk[]>(
            new BoundedChannelOptions(ChunkChannelCapacity) { FullMode = BoundedChannelFullMode.Wait });
        var embedToIndex = Channel.CreateBounded<DocChunk[]>(
            new BoundedChannelOptions(ChunkChannelCapacity) { FullMode = BoundedChannelFullMode.Wait });

        // Shared progress record
        var progress = jobRecord ?? new ScrapeJobRecord
        {
            Id = Guid.NewGuid().ToString(),
            Job = job,
            Profile = profile
        };
        progress.PipelineState = "Running";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Launch all five stages
        var crawlTask = RunCrawlStageAsync(job, crawlToClassify.Writer, resumeUrls, progress, onProgress, cts);
        var classifyTask = RunClassifyStageAsync(job, crawlToClassify.Reader, classifyToChunk.Writer, progress, onProgress, cts);
        var chunkTask = RunChunkStageAsync(classifyToChunk.Reader, chunkToEmbed.Writer, progress, onProgress, cts);
        var embedTask = RunEmbedStageAsync(chunkToEmbed.Reader, embedToIndex.Writer, progress, onProgress, cts);
        var indexTask = RunIndexStageAsync(profile, job, embedToIndex.Reader, progress, onProgress, cts);

        try
        {
            await Task.WhenAll(crawlTask, classifyTask, chunkTask, embedTask, indexTask);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mLogger.LogError(ex, "Pipeline failed for {LibraryId} v{Version}", job.LibraryId, job.Version);
            progress.PipelineState = "Failed";
            progress.ErrorMessage = ex.Message;
            onProgress?.Invoke(progress);
            throw;
        }

        // Update library metadata
        await UpdateLibraryMetadataAsync(job, progress, ct);

        progress.PipelineState = "Completed";
        onProgress?.Invoke(progress);

        mLogger.LogInformation(
            "Streaming ingestion complete for {LibraryId} v{Version}: {Pages} pages, {Chunks} chunks searchable",
            job.LibraryId, job.Version, progress.PagesCompleted, progress.ChunksCompleted);
    }

    #region Crawl stage

    private async Task RunCrawlStageAsync(
        ScrapeJob job,
        ChannelWriter<PageRecord> output,
        IReadOnlySet<string>? resumeUrls,
        ScrapeJobRecord progress,
        Action<ScrapeJobRecord>? onProgress,
        CancellationTokenSource cts)
    {
        try
        {
            await mCrawler.CrawlAsync(
                job,
                output,
                resumeUrls,
                pageCount =>
                {
                    progress.PagesFetched = pageCount;
                    onProgress?.Invoke(progress);
                },
                queueCount =>
                {
                    progress.PagesQueued = queueCount;
                },
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            output.TryComplete();
            throw;
        }
        catch (Exception ex)
        {
            mLogger.LogError(ex, "Crawl stage fatal error");
            output.TryComplete(ex);
            await cts.CancelAsync();
            throw;
        }
    }

    #endregion

    #region Classify stage

    private async Task RunClassifyStageAsync(
        ScrapeJob job,
        ChannelReader<PageRecord> input,
        ChannelWriter<PageRecord> output,
        ScrapeJobRecord progress,
        Action<ScrapeJobRecord>? onProgress,
        CancellationTokenSource cts)
    {
        try
        {
            await foreach (var page in input.ReadAllAsync(cts.Token))
            {
                var classified = await ClassifyPageAsync(page, job, progress);
                await output.WriteAsync(classified, cts.Token);
                progress.PagesClassified++;
                onProgress?.Invoke(progress);
            }
        }
        catch (OperationCanceledException)
        {
            output.TryComplete();
            throw;
        }
        catch (Exception ex)
        {
            mLogger.LogError(ex, "Classify stage fatal error");
            output.TryComplete(ex);
            await cts.CancelAsync();
            throw;
        }
        finally
        {
            output.TryComplete();
        }
    }

    private async Task<PageRecord> ClassifyPageAsync(PageRecord page, ScrapeJob job, ScrapeJobRecord progress)
    {
        PageRecord result;
        try
        {
            var (category, confidence) = await mLlmClassifier.ClassifyAsync(page, job.LibraryHint);
            if (category != DocCategory.Unclassified && confidence > 0)
            {
                result = page with { Category = category };
                await mPageRepository.UpsertPageAsync(result);
            }
            else
                result = page;
        }
        catch (Exception ex)
        {
            mLogger.LogWarning(ex, "LLM classification failed for {Url}, passing as Unclassified", page.Url);
            progress.ErrorCount++;
            result = page;
        }
        return result;
    }

    #endregion

    #region Chunk stage

    private async Task RunChunkStageAsync(
        ChannelReader<PageRecord> input,
        ChannelWriter<DocChunk[]> output,
        ScrapeJobRecord progress,
        Action<ScrapeJobRecord>? onProgress,
        CancellationTokenSource cts)
    {
        try
        {
            await foreach (var page in input.ReadAllAsync(cts.Token))
            {
                try
                {
                    var chunks = mChunker.Chunk(page);
                    if (chunks.Count > 0)
                    {
                        await output.WriteAsync(chunks.ToArray(), cts.Token);
                        progress.ChunksGenerated += chunks.Count;
                        onProgress?.Invoke(progress);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    mLogger.LogWarning(ex, "Chunking failed for {Url}, skipping page", page.Url);
                    progress.ErrorCount++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            output.TryComplete();
            throw;
        }
        catch (Exception ex)
        {
            mLogger.LogError(ex, "Chunk stage fatal error");
            output.TryComplete(ex);
            await cts.CancelAsync();
            throw;
        }
        finally
        {
            output.TryComplete();
        }
    }

    #endregion

    #region Embed stage

    private async Task RunEmbedStageAsync(
        ChannelReader<DocChunk[]> input,
        ChannelWriter<DocChunk[]> output,
        ScrapeJobRecord progress,
        Action<ScrapeJobRecord>? onProgress,
        CancellationTokenSource cts)
    {
        var batch = new List<DocChunk>();

        try
        {
            await foreach (var pageChunks in input.ReadAllAsync(cts.Token))
            {
                batch.AddRange(pageChunks);

                while (batch.Count >= EmbedBatchSize)
                {
                    var toEmbed = batch.Take(EmbedBatchSize).ToList();
                    batch = batch.Skip(EmbedBatchSize).ToList();
                    await EmbedAndForwardBatchAsync(toEmbed, output, progress, onProgress, cts.Token);
                }
            }

            // Flush remaining chunks
            if (batch.Count > 0)
                await EmbedAndForwardBatchAsync(batch, output, progress, onProgress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            output.TryComplete();
            throw;
        }
        catch (Exception ex)
        {
            mLogger.LogError(ex, "Embed stage fatal error");
            output.TryComplete(ex);
            await cts.CancelAsync();
            throw;
        }
        finally
        {
            output.TryComplete();
        }
    }

    private async Task EmbedAndForwardBatchAsync(
        List<DocChunk> batch,
        ChannelWriter<DocChunk[]> output,
        ScrapeJobRecord progress,
        Action<ScrapeJobRecord>? onProgress,
        CancellationToken ct)
    {
        try
        {
            var texts = batch
                .Select(c => TruncateForEmbedding(
                    $"[{c.Category}] [{c.LibraryId}] [{c.PageTitle}]\n{c.Content}",
                    MaxEmbedChars))
                .ToList();

            var embeddings = await EmbedWithRetryAsync(texts, ct);

            var embeddedChunks = new DocChunk[batch.Count];
            for (int i = 0; i < batch.Count; i++)
                embeddedChunks[i] = batch[i] with { Embedding = embeddings[i] };

            // Upsert to MongoDB (supports resume — no duplicates on re-run)
            await mChunkRepository.UpsertChunksAsync(embeddedChunks, ct);
            progress.ChunksEmbedded += embeddedChunks.Length;
            onProgress?.Invoke(progress);

            await output.WriteAsync(embeddedChunks, ct);

            mLogger.LogDebug("Embedded and stored batch of {Count} chunks", embeddedChunks.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mLogger.LogWarning(ex, "Embedding failed for batch of {Count} chunks, skipping", batch.Count);
            progress.ErrorCount++;
        }
    }

    private async Task<float[][]> EmbedWithRetryAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        float[][] result;
        try
        {
            result = await mEmbeddingProvider.EmbedAsync(texts, ct);
        }
        catch (Exception ex)
        {
            mLogger.LogWarning(ex, "Embedding failed, retrying once");
            result = await mEmbeddingProvider.EmbedAsync(texts, ct);
        }
        return result;
    }

    #endregion

    #region Index stage

    private async Task RunIndexStageAsync(
        string? profile,
        ScrapeJob job,
        ChannelReader<DocChunk[]> input,
        ScrapeJobRecord progress,
        Action<ScrapeJobRecord>? onProgress,
        CancellationTokenSource cts)
    {
        var pendingChunks = new List<DocChunk>();
        // Track chunks per page for PagesCompleted counting
        var pageChunkCounts = new Dictionary<string, int>();
        var pageChunksIndexed = new Dictionary<string, int>();

        try
        {
            await foreach (var embeddedChunks in input.ReadAllAsync(cts.Token))
            {
                pendingChunks.AddRange(embeddedChunks);
                progress.ChunksCompleted += embeddedChunks.Length;

                // Track page completion
                foreach (var chunk in embeddedChunks)
                {
                    pageChunksIndexed.TryGetValue(chunk.PageUrl, out int indexed);
                    pageChunksIndexed[chunk.PageUrl] = indexed + embeddedChunks.Length;
                }

                if (pendingChunks.Count >= IndexRebuildInterval)
                {
                    await RebuildIndexAsync(profile, job, pendingChunks, cts.Token);
                    pendingChunks.Clear();
                    progress.PagesCompleted = pageChunksIndexed.Count;
                    onProgress?.Invoke(progress);
                }
            }

            // Final rebuild with remaining chunks
            if (pendingChunks.Count > 0)
            {
                await RebuildIndexAsync(profile, job, pendingChunks, cts.Token);
                progress.PagesCompleted = pageChunksIndexed.Count;
                onProgress?.Invoke(progress);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            mLogger.LogError(ex, "Index stage fatal error");
            await cts.CancelAsync();
            throw;
        }
    }

    private async Task RebuildIndexAsync(
        string? profile,
        ScrapeJob job,
        List<DocChunk> chunks,
        CancellationToken ct)
    {
        try
        {
            await mVectorSearch.IndexChunksAsync(profile, job.LibraryId, job.Version, chunks, ct);
            mLogger.LogInformation("Rebuilt vector index with {Count} new chunks", chunks.Count);
        }
        catch (Exception ex)
        {
            mLogger.LogWarning(ex, "Vector index rebuild failed, will retry on next batch");
        }
    }

    #endregion

    #region Library metadata

    private async Task UpdateLibraryMetadataAsync(ScrapeJob job, ScrapeJobRecord progress, CancellationToken ct)
    {
        var library = await mLibraryRepository.GetLibraryAsync(job.LibraryId, ct);
        if (library == null)
        {
            library = new LibraryRecord
            {
                Id = job.LibraryId,
                Name = job.LibraryId,
                Hint = job.LibraryHint,
                CurrentVersion = job.Version,
                AllVersions = [job.Version]
            };
        }
        else
        {
            library.CurrentVersion = job.Version;
            if (!library.AllVersions.Contains(job.Version))
                library.AllVersions.Add(job.Version);
        }
        await mLibraryRepository.UpsertLibraryAsync(library, ct);

        var versionRecord = new LibraryVersionRecord
        {
            Id = $"{job.LibraryId}/{job.Version}",
            LibraryId = job.LibraryId,
            Version = job.Version,
            ScrapedAt = DateTime.UtcNow,
            PageCount = progress.PagesFetched,
            ChunkCount = progress.ChunksCompleted,
            EmbeddingProviderId = mEmbeddingProvider.ProviderId,
            EmbeddingModelName = mEmbeddingProvider.ModelName,
            EmbeddingDimensions = mEmbeddingProvider.Dimensions
        };
        await mLibraryRepository.UpsertVersionAsync(versionRecord, ct);
    }

    #endregion

    private static string TruncateForEmbedding(string text, int maxChars)
    {
        var result = text.Length > maxChars ? text[..maxChars] : text;
        return result;
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build E:\Projects\RAG\SaddleRAG.Ingestion\SaddleRAG.Ingestion.csproj`
Expected: PASS (or errors in ScrapeJobRunner/CLI/MCP that still reference old signatures — those are fixed in Tasks 7-8).

- [ ] **Step 3: Commit**

Commit message: `feat: rewrite IngestionOrchestrator as channel-based streaming pipeline`

---

### Task 7: Update ScrapeJobRunner for streaming progress

The runner's progress callback signature changed: it now receives the full `ScrapeJobRecord` rather than four separate ints.

**Files:**
- Modify: `SaddleRAG.Ingestion/ScrapeJobRunner.cs`

- [ ] **Step 1: Update RunJobAsync to use new IngestAsync signature**

In `ScrapeJobRunner.cs`, replace the `RunJobAsync` method's orchestrator call block (lines 91-101) from:

```csharp
            await mOrchestrator.IngestAsync(
                jobRecord.Job,
                jobRecord.Profile,
                (phase, pageCount, chunkCount, embeddedCount) =>
                {
                    jobRecord.CurrentPhase = phase;
                    jobRecord.PagesFetched = pageCount;
                    jobRecord.ChunksGenerated = chunkCount;
                    jobRecord.ChunksEmbedded = embeddedCount;
                    mJobRepository.UpsertAsync(jobRecord).GetAwaiter().GetResult();
                });
```

to:

```csharp
            await mOrchestrator.IngestAsync(
                jobRecord.Job,
                jobRecord.Profile,
                forceClean: false,
                onProgress: updatedRecord =>
                {
                    jobRecord.PipelineState = updatedRecord.PipelineState;
                    jobRecord.PagesQueued = updatedRecord.PagesQueued;
                    jobRecord.PagesFetched = updatedRecord.PagesFetched;
                    jobRecord.PagesClassified = updatedRecord.PagesClassified;
                    jobRecord.ChunksGenerated = updatedRecord.ChunksGenerated;
                    jobRecord.ChunksEmbedded = updatedRecord.ChunksEmbedded;
                    jobRecord.ChunksCompleted = updatedRecord.ChunksCompleted;
                    jobRecord.PagesCompleted = updatedRecord.PagesCompleted;
                    jobRecord.ErrorCount = updatedRecord.ErrorCount;
                    mJobRepository.UpsertAsync(jobRecord).GetAwaiter().GetResult();
                },
                jobRecord: jobRecord);
```

- [ ] **Step 2: Update the status setting after completion**

Replace:

```csharp
            jobRecord.Status = ScrapeJobStatus.Completed;
            jobRecord.CurrentPhase = "Completed";
            jobRecord.CompletedAt = DateTime.UtcNow;
```

with:

```csharp
            jobRecord.Status = ScrapeJobStatus.Completed;
            jobRecord.PipelineState = "Completed";
            jobRecord.CompletedAt = DateTime.UtcNow;
```

- [ ] **Step 3: Update the error handling**

In the catch block, after `jobRecord.ErrorMessage = ex.Message;` add:

```csharp
            jobRecord.PipelineState = "Failed";
```

- [ ] **Step 4: Update the initial phase setting**

Replace:

```csharp
            jobRecord.CurrentPhase = "Starting ingestion";
```

with:

```csharp
            jobRecord.PipelineState = "Starting";
```

- [ ] **Step 5: Build to verify**

Run: `dotnet build E:\Projects\RAG\SaddleRAG.Ingestion\SaddleRAG.Ingestion.csproj`
Expected: PASS

- [ ] **Step 6: Commit**

Commit message: `refactor: update ScrapeJobRunner for streaming pipeline progress reporting`

---

### Task 8: Update MCP tools for new progress fields and add continue_scrape

Update `GetScrapeStatus` to report new counters, `ListScrapeJobs` to use `PipelineState`, and add the `continue_scrape` tool.

**Files:**
- Modify: `SaddleRAG.Mcp/Tools/IngestionTools.cs`
- Modify: `SaddleRAG.Mcp/Tools/ScrapeDocsTools.cs`

- [ ] **Step 1: Update GetScrapeStatus to include new fields**

In `IngestionTools.cs`, replace the `response` anonymous object in `GetScrapeStatus` from:

```csharp
            var response = new
            {
                job.Id,
                job.Status,
                job.CurrentPhase,
                job.PagesFetched,
                job.ChunksGenerated,
                job.ChunksEmbedded,
                job.ErrorMessage,
                job.CreatedAt,
                job.StartedAt,
                job.CompletedAt,
                Library = job.Job.LibraryId,
                Version = job.Job.Version
            };
```

with:

```csharp
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
                Version = job.Job.Version
            };
```

- [ ] **Step 2: Update ListScrapeJobs to use PipelineState**

In `IngestionTools.cs`, replace `j.CurrentPhase` in the `ListScrapeJobs` projection:

```csharp
            j.CurrentPhase,
```

with:

```csharp
            j.PipelineState,
```

- [ ] **Step 3: Add continue_scrape MCP tool to ScrapeDocsTools**

Add this method to `ScrapeDocsTools`, after the `ScrapeDocs` method:

```csharp
    /// <summary>
    ///     Continue a previously interrupted or MaxPages-limited scrape.
    ///     Retrieves the original job config and resumes from where it left off.
    /// </summary>
    [McpServerTool(Name = "continue_scrape")]
    [Description(
        "Continue a previously interrupted or MaxPages-limited scrape. " +
        "Retrieves the original job configuration from the most recent scrape " +
        "for this library+version and resumes crawling from where it left off — " +
        "already-indexed pages are skipped automatically.")]
    public static async Task<string> ContinueScrape(
        ScrapeJobRunner runner,
        RepositoryFactory repositoryFactory,
        [Description("Library identifier to continue scraping")] string libraryId,
        [Description("Version string to continue scraping")] string version,
        [Description("Optional database profile name")] string? profile = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var jobRepo = repositoryFactory.GetScrapeJobRepository(profile);
        var recentJobs = await jobRepo.ListRecentAsync(100, ct);
        var previousJob = recentJobs
            .Where(j => j.Job.LibraryId == libraryId && j.Job.Version == version)
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefault();

        string json;
        if (previousJob == null)
        {
            var notFound = new
            {
                Status = "NotFound",
                Message = $"No previous scrape job found for {libraryId} v{version}. " +
                          "Use scrape_docs or scrape_library to start a new scrape."
            };
            json = JsonSerializer.Serialize(notFound, new JsonSerializerOptions { WriteIndented = true });
        }
        else
        {
            var jobId = await runner.QueueAsync(previousJob.Job, profile, ct);

            var response = new
            {
                JobId = jobId,
                Status = "Queued",
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
```

- [ ] **Step 4: Build the full solution**

Run: `dotnet build E:\Projects\RAG\SaddleRAG.sln`
Expected: May have errors in CLI Program.cs — fixed in Task 9.

- [ ] **Step 5: Commit**

Commit message: `feat: update MCP tools for streaming progress and add continue_scrape tool`

---

### Task 9: Update CLI Program.cs

Remove HeuristicClassifier reference (if not already done), fix any remaining references to old `CurrentPhase` or `IngestAsync` signature.

**Files:**
- Modify: `SaddleRAG.Cli/Program.cs`

- [ ] **Step 1: Search for remaining CurrentPhase references in CLI**

Run: `grep -n "CurrentPhase" E:\Projects\RAG\SaddleRAG.Cli\Program.cs`

Fix any occurrences by replacing `CurrentPhase` with `PipelineState`.

- [ ] **Step 2: Search for remaining onProgress callback references**

Run: `grep -n "onProgress\|Action<string, int, int, int>" E:\Projects\RAG\SaddleRAG.Cli\Program.cs`

The CLI's `ingest` command likely calls `IngestAsync` directly. Update any progress callback to match the new signature `Action<ScrapeJobRecord>`.

If the CLI calls `orchestrator.IngestAsync(job, ...)` with the old four-param callback, replace with:

```csharp
await orchestrator.IngestAsync(
    job,
    profile: null,
    forceClean: false,
    onProgress: progress =>
    {
        Console.Write($"\rQueued: {progress.PagesQueued} | Crawled: {progress.PagesFetched} | " +
                      $"Classified: {progress.PagesClassified} | Chunks: {progress.ChunksGenerated} | " +
                      $"Searchable: {progress.ChunksCompleted} chunks ({progress.PagesCompleted} pages)");
    });
Console.WriteLine();
```

- [ ] **Step 3: Build the full solution**

Run: `dotnet build E:\Projects\RAG\SaddleRAG.sln`
Expected: PASS — all projects compile.

- [ ] **Step 4: Commit**

Commit message: `refactor: update CLI for streaming pipeline progress reporting`

---

### Task 10: Run tests and fix any failures

Run the full test suite to catch any breakage from the refactor.

**Files:**
- Possibly modify: any test files that reference removed types or changed signatures

- [ ] **Step 1: Run all tests**

Run: `dotnet test E:\Projects\RAG\SaddleRAG.Tests\SaddleRAG.Tests.csproj`
Expected: All tests pass. If any fail due to references to `HeuristicClassifier` or `CurrentPhase`, fix them.

- [ ] **Step 2: Commit any test fixes**

Commit message: `fix: update tests for streaming pipeline refactor`

---

### Task 11: Final build and solution-wide verification

Ensure the entire solution builds and all tests pass.

- [ ] **Step 1: Clean build the full solution**

Run: `dotnet build E:\Projects\RAG\SaddleRAG.sln --no-incremental`
Expected: PASS, zero warnings related to our changes.

- [ ] **Step 2: Run all tests**

Run: `dotnet test E:\Projects\RAG\SaddleRAG.Tests\SaddleRAG.Tests.csproj`
Expected: All tests pass.

- [ ] **Step 3: Commit if any final fixes were needed**

Commit message: `chore: final cleanup for streaming pipeline refactor`
