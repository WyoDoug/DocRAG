// IngestionOrchestrator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.Threading.Channels;
using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using DocRAG.Database.Repositories;
using DocRAG.Ingestion.Chunking;
using DocRAG.Ingestion.Classification;
using DocRAG.Ingestion.Crawling;
using DocRAG.Ingestion.Embedding;
using DocRAG.Ingestion.Suspect;
using Microsoft.Extensions.Logging;

#endregion

namespace DocRAG.Ingestion;

/// <summary>
///     Orchestrates the streaming ingestion pipeline:
///     crawl â†’ classify â†’ chunk â†’ embed â†’ index.
///     Each stage runs as a single async consumer connected by bounded channels.
/// </summary>
public class IngestionOrchestrator
{
    public IngestionOrchestrator(PageCrawler crawler,
                                 LlmClassifier llmClassifier,
                                 CategoryAwareChunker chunker,
                                 IEmbeddingProvider embeddingProvider,
                                 IVectorSearchProvider vectorSearch,
                                 ILibraryRepository libraryRepository,
                                 IPageRepository pageRepository,
                                 IChunkRepository chunkRepository,
                                 ILibraryProfileRepository libraryProfileRepository,
                                 ILibraryIndexRepository libraryIndexRepository,
                                 IBm25ShardRepository bm25ShardRepository,
                                 SuspectDetector suspectDetector,
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
        mLibraryProfileRepository = libraryProfileRepository;
        mLibraryIndexRepository = libraryIndexRepository;
        mBm25ShardRepository = bm25ShardRepository;
        mSuspectDetector = suspectDetector;
        mLogger = logger;
    }

    private readonly CategoryAwareChunker mChunker;
    private readonly IChunkRepository mChunkRepository;
    private readonly ILibraryProfileRepository mLibraryProfileRepository;
    private readonly ILibraryIndexRepository mLibraryIndexRepository;
    private readonly IBm25ShardRepository mBm25ShardRepository;
    private readonly SuspectDetector mSuspectDetector;

    private readonly PageCrawler mCrawler;
    private readonly IEmbeddingProvider mEmbeddingProvider;
    private readonly ILibraryRepository mLibraryRepository;
    private readonly LlmClassifier mLlmClassifier;
    private readonly ILogger<IngestionOrchestrator> mLogger;
    private readonly IPageRepository mPageRepository;
    private readonly IVectorSearchProvider mVectorSearch;

    /// <summary>
    ///     Run the streaming ingestion pipeline for a scrape job.
    /// </summary>
    public async Task IngestAsync(ScrapeJob job,
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
                                   job.LibraryId,
                                   job.Version
                                  );
        }

        // Create bounded channels
        var crawlToClassify = Channel.CreateBounded<PageRecord>(new BoundedChannelOptions(PageChannelCapacity)
                                                                    { FullMode = BoundedChannelFullMode.Wait }
                                                               );
        var classifyToChunk = Channel.CreateBounded<PageRecord>(new BoundedChannelOptions(PageChannelCapacity)
                                                                    { FullMode = BoundedChannelFullMode.Wait }
                                                               );
        var chunkToEmbed = Channel.CreateBounded<DocChunk[]>(new BoundedChannelOptions(ChunkChannelCapacity)
                                                                 { FullMode = BoundedChannelFullMode.Wait }
                                                            );
        var embedToIndex = Channel.CreateBounded<DocChunk[]>(new BoundedChannelOptions(ChunkChannelCapacity)
                                                                 { FullMode = BoundedChannelFullMode.Wait }
                                                            );

        // Shared progress record
        var progress = jobRecord ??
                       new ScrapeJobRecord
                           {
                               Id = Guid.NewGuid().ToString(),
                               Job = job,
                               Profile = profile
                           };
        progress.PipelineState = nameof(ScrapeJobStatus.Running);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Launch all five stages
        var crawlTask = RunCrawlStageAsync(job,
                                           crawlToClassify.Writer,
                                           resumeUrls,
                                           progress,
                                           onProgress,
                                           cts
                                          );
        var classifyTask =
            RunClassifyStageAsync(job,
                                  crawlToClassify.Reader,
                                  classifyToChunk.Writer,
                                  progress,
                                  onProgress,
                                  cts
                                 );
        var chunkTask = RunChunkStageAsync(classifyToChunk.Reader, chunkToEmbed.Writer, progress, onProgress, cts);
        var embedTask = RunEmbedStageAsync(chunkToEmbed.Reader, embedToIndex.Writer, progress, onProgress, cts);
        var indexTask = RunIndexStageAsync(profile,
                                           job,
                                           embedToIndex.Reader,
                                           progress,
                                           onProgress,
                                           cts
                                          );

        try
        {
            await Task.WhenAll(crawlTask, classifyTask, chunkTask, embedTask, indexTask);
        }
        catch(Exception ex) when(ex is not OperationCanceledException)
        {
            mLogger.LogError(ex, "Pipeline failed for {LibraryId} v{Version}", job.LibraryId, job.Version);
            progress.PipelineState = nameof(ScrapeJobStatus.Failed);
            progress.ErrorMessage = ex.Message;
            onProgress?.Invoke(progress);
            throw;
        }

        // Build BM25 index over the freshly persisted chunks so hybrid
        // search has both signals available without a follow-up rescrub.
        await BuildBm25IndexAsync(job, ct);

        // Update library metadata
        await UpdateLibraryMetadataAsync(job, progress, ct);

        progress.PipelineState = nameof(ScrapeJobStatus.Completed);
        onProgress?.Invoke(progress);

        mLogger.LogInformation("Streaming ingestion complete for {LibraryId} v{Version}: {Pages} pages, {Chunks} chunks searchable",
                               job.LibraryId,
                               job.Version,
                               progress.PagesCompleted,
                               progress.ChunksCompleted
                              );
    }

    #region Single-page top-up

    /// <summary>
    ///     Ingest one URL into an existing (library, version) without
    ///     re-crawling. Fetches the page through the same Playwright
    ///     path as a regular scrape, classifies it, chunks it, embeds
    ///     the chunks, upserts them, and refreshes the BM25 index over
    ///     the full chunk corpus so search picks the new content up
    ///     immediately.
    /// </summary>
    public async Task<SinglePageIngestResult> IngestSinglePageAsync(string libraryId,
                                                                    string version,
                                                                    string url,
                                                                    string? profile = null,
                                                                    CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(url);

        mLogger.LogInformation("Adding single page {Url} to {LibraryId} v{Version}", url, libraryId, version);

        var page = await mCrawler.FetchSinglePageAsync(libraryId, version, url, ct);

        SinglePageIngestResult result;
        if (page == null)
        {
            result = new SinglePageIngestResult
                         {
                             Status = SinglePageStatusFailed,
                             Url = url,
                             Library = libraryId,
                             Version = version,
                             Reason = "Fetch failed after retries (likely WAF block or persistent error)."
                         };
        }
        else
            result = await ProcessSinglePageAsync(page, libraryId, version, url, ct);

        return result;
    }

    private async Task<SinglePageIngestResult> ProcessSinglePageAsync(PageRecord page,
                                                                      string libraryId,
                                                                      string version,
                                                                      string url,
                                                                      CancellationToken ct)
    {
        var classified = await ClassifySinglePageAsync(page, libraryHint: libraryId);
        var chunks = mChunker.Chunk(classified);

        SinglePageIngestResult result;
        if (chunks.Count == 0)
        {
            result = new SinglePageIngestResult
                         {
                             Status = SinglePageStatusEmpty,
                             Url = url,
                             Library = libraryId,
                             Version = version,
                             Reason = "Page fetched but produced zero chunks (empty or filtered content)."
                         };
        }
        else
            result = await PersistSinglePageChunksAsync(classified, chunks.ToList(), libraryId, version, url, ct);

        return result;
    }

    private async Task<SinglePageIngestResult> PersistSinglePageChunksAsync(PageRecord classified,
                                                                             List<DocChunk> chunks,
                                                                             string libraryId,
                                                                             string version,
                                                                             string url,
                                                                             CancellationToken ct)
    {
        var embedded = await EmbedSinglePageChunksAsync(chunks, ct);
        await mChunkRepository.UpsertChunksAsync(embedded, ct);

        var bm25Job = new ScrapeJob
                          {
                              RootUrl = url,
                              LibraryId = libraryId,
                              Version = version,
                              LibraryHint = libraryId,
                              AllowedUrlPatterns = []
                          };
        await BuildBm25IndexAsync(bm25Job, ct);

        var result = new SinglePageIngestResult
                         {
                             Status = SinglePageStatusIndexed,
                             Url = url,
                             Library = libraryId,
                             Version = version,
                             ChunksAdded = embedded.Length,
                             Category = classified.Category.ToString()
                         };
        return result;
    }

    private async Task<PageRecord> ClassifySinglePageAsync(PageRecord page, string libraryHint)
    {
        PageRecord result;
        try
        {
            (var category, float confidence) = await mLlmClassifier.ClassifyAsync(page, libraryHint);
            if (category != DocCategory.Unclassified && confidence > 0)
            {
                result = page with { Category = category };
                await mPageRepository.UpsertPageAsync(result);
            }
            else
                result = page;
        }
        catch(Exception ex)
        {
            mLogger.LogWarning(ex, "Single-page classification failed for {Url}, leaving Unclassified", page.Url);
            result = page;
        }

        return result;
    }

    private async Task<DocChunk[]> EmbedSinglePageChunksAsync(List<DocChunk> chunks, CancellationToken ct)
    {
        var texts = chunks
                    .Select(c =>
                                TruncateForEmbedding($"[{c.Category}] [{c.LibraryId}] [{c.PageTitle}]\n{c.Content}",
                                                     MaxEmbedChars
                                                    )
                           )
                    .ToList();

        float[][] embeddings = await EmbedWithRetryAsync(texts, ct);

        var embedded = new DocChunk[chunks.Count];
        for(var i = 0; i < chunks.Count; i++)
            embedded[i] = chunks[i] with { Embedding = embeddings[i] };

        return embedded;
    }

    #endregion

    #region BM25 index

    /// <summary>
    ///     Build the sharded BM25 inverted index over the chunks just
    ///     persisted by the embed/index stage, then upsert the matching
    ///     <see cref="LibraryIndex"/> with the inline stats.
    ///     If a prior index exists (e.g. from a previous rescrub), its
    ///     <c>CodeFenceSymbols</c> and <c>Manifest</c> fields are preserved
    ///     so a re-scrape doesn't blow away symbol-extraction state until
    ///     the next rescrub recomputes it.
    /// </summary>
    private async Task BuildBm25IndexAsync(ScrapeJob job, CancellationToken ct)
    {
        var chunks = await mChunkRepository.GetChunksAsync(job.LibraryId, job.Version, ct);
        if (chunks.Count == 0)
        {
            mLogger.LogWarning("BM25 build skipped for {LibraryId} v{Version}: no chunks persisted",
                               job.LibraryId,
                               job.Version
                              );
        }
        else
            await PersistBm25IndexAsync(job, chunks, ct);
    }

    private async Task PersistBm25IndexAsync(ScrapeJob job, IReadOnlyList<DocChunk> chunks, CancellationToken ct)
    {
        var build = Bm25IndexBuilder.Build(job.LibraryId, job.Version, chunks);
        await mBm25ShardRepository.ReplaceShardsAsync(job.LibraryId, job.Version, build.Shards, ct);

        var existing = await mLibraryIndexRepository.GetAsync(job.LibraryId, job.Version, ct);
        var index = new LibraryIndex
                        {
                            Id = LibraryIndexRepository.MakeId(job.LibraryId, job.Version),
                            LibraryId = job.LibraryId,
                            Version = job.Version,
                            Bm25 = build.Stats,
                            CodeFenceSymbols = existing?.CodeFenceSymbols ?? [],
                            Manifest = existing?.Manifest ?? new LibraryManifest()
                        };
        await mLibraryIndexRepository.UpsertAsync(index, ct);

        mLogger.LogInformation("BM25 index built for {LibraryId} v{Version}: {Docs} docs, {Shards} shards, avgLen={AvgLen:F1}",
                               job.LibraryId,
                               job.Version,
                               build.Stats.DocumentCount,
                               build.Stats.ShardCount,
                               build.Stats.AverageDocLength
                              );
    }

    #endregion

    #region Crawl stage

    private async Task RunCrawlStageAsync(ScrapeJob job,
                                          ChannelWriter<PageRecord> output,
                                          IReadOnlySet<string>? resumeUrls,
                                          ScrapeJobRecord progress,
                                          Action<ScrapeJobRecord>? onProgress,
                                          CancellationTokenSource cts)
    {
        try
        {
            await mCrawler.CrawlAsync(job,
                                      output,
                                      resumeUrls,
                                      pageCount =>
                                      {
                                          progress.PagesFetched = pageCount;
                                          onProgress?.Invoke(progress);
                                      },
                                      queueCount => { progress.PagesQueued = queueCount; },
                                      () =>
                                      {
                                          progress.IncrementErrorCount();
                                          onProgress?.Invoke(progress);
                                      },
                                      cts.Token
                                     );
        }
        catch(OperationCanceledException)
        {
            output.TryComplete();
            throw;
        }
        catch(Exception ex)
        {
            mLogger.LogError(ex, "Crawl stage fatal error");
            output.TryComplete(ex);
            await cts.CancelAsync();
            throw;
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
        await EvaluateSuspectAsync(job, progress, ct);
    }

    private async Task EvaluateSuspectAsync(ScrapeJob job, ScrapeJobRecord progress, CancellationToken ct)
    {
        var languageMix = await mChunkRepository.GetLanguageMixAsync(job.LibraryId, job.Version, ct);
        var hostnameDist = await mChunkRepository.GetHostnameDistributionAsync(job.LibraryId, job.Version, ct);
        var sampleTitles = await mChunkRepository.GetSampleTitlesAsync(job.LibraryId, job.Version, SuspectSampleTitleLimit, ct);

        var profile = await mLibraryProfileRepository.GetAsync(job.LibraryId, job.Version, ct);
        var declaredLanguages = profile?.Languages ?? Array.Empty<string>();

        // distinctLinkTargets: SparseLinkGraph disabled until an outbound-link count helper exists.
        // Passed as int.MaxValue so it never triggers; the other four reasons cover the common cases.
        var reasons = await mSuspectDetector.EvaluateAsync(job.LibraryId,
                                                           job.Version,
                                                           job.RootUrl,
                                                           pageCount: progress.PagesCompleted,
                                                           distinctHostCount: hostnameDist.Count,
                                                           distinctLinkTargets: int.MaxValue,
                                                           languageMix: languageMix,
                                                           declaredLanguages: declaredLanguages,
                                                           sampleTitles: sampleTitles,
                                                           ct);

        if (reasons.Count > 0)
            await mLibraryRepository.SetSuspectAsync(job.LibraryId, job.Version, reasons, ct);
        else
            await mLibraryRepository.ClearSuspectAsync(job.LibraryId, job.Version, ct);
    }

    #endregion

    private static string TruncateForEmbedding(string text, int maxChars)
    {
        string result = text.Length > maxChars ? text[..maxChars] : text;
        return result;
    }

    private const int SuspectSampleTitleLimit = 5;

    private const int PageChannelCapacity = 50;
    private const int ChunkChannelCapacity = 20;
    private const int EmbedBatchSize = 32;

    private const int IndexRebuildInterval = 100;

    // Safety limit for embedding context window (nomic-embed-text: 2048 tokens)
    // Use 6000 chars as hard cap (~2000 tokens at ~3 chars/token)
    private const int MaxEmbedChars = 6000;

    private const string SinglePageStatusIndexed = "Indexed";
    private const string SinglePageStatusEmpty = "Empty";
    private const string SinglePageStatusFailed = "Failed";

    #region Classify stage

    private async Task RunClassifyStageAsync(ScrapeJob job,
                                             ChannelReader<PageRecord> input,
                                             ChannelWriter<PageRecord> output,
                                             ScrapeJobRecord progress,
                                             Action<ScrapeJobRecord>? onProgress,
                                             CancellationTokenSource cts)
    {
        try
        {
            await foreach(var page in input.ReadAllAsync(cts.Token))
            {
                var classified = await ClassifyPageAsync(page, job, progress);
                await output.WriteAsync(classified, cts.Token);
                progress.PagesClassified++;
                onProgress?.Invoke(progress);
            }
        }
        catch(OperationCanceledException)
        {
            output.TryComplete();
            throw;
        }
        catch(Exception ex)
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
            (var category, float confidence) = await mLlmClassifier.ClassifyAsync(page, job.LibraryHint);
            if (category != DocCategory.Unclassified && confidence > 0)
            {
                result = page with { Category = category };
                await mPageRepository.UpsertPageAsync(result);
            }
            else
                result = page;
        }
        catch(Exception ex)
        {
            mLogger.LogWarning(ex, "LLM classification failed for {Url}, passing as Unclassified", page.Url);
            progress.IncrementErrorCount();
            result = page;
        }

        return result;
    }

    #endregion

    #region Chunk stage

    private async Task RunChunkStageAsync(ChannelReader<PageRecord> input,
                                          ChannelWriter<DocChunk[]> output,
                                          ScrapeJobRecord progress,
                                          Action<ScrapeJobRecord>? onProgress,
                                          CancellationTokenSource cts)
    {
        try
        {
            await foreach(var page in input.ReadAllAsync(cts.Token))
                await ChunkPageAsync(page, output, progress, onProgress, cts.Token);
        }
        catch(OperationCanceledException)
        {
            output.TryComplete();
            throw;
        }
        catch(Exception ex)
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

    private async Task ChunkPageAsync(PageRecord page,
                                      ChannelWriter<DocChunk[]> output,
                                      ScrapeJobRecord progress,
                                      Action<ScrapeJobRecord>? onProgress,
                                      CancellationToken ct)
    {
        try
        {
            var chunks = mChunker.Chunk(page);
            if (chunks.Count > 0)
            {
                await output.WriteAsync(chunks.ToArray(), ct);
                progress.ChunksGenerated += chunks.Count;
                onProgress?.Invoke(progress);
            }
        }
        catch(Exception ex) when(ex is not OperationCanceledException)
        {
            mLogger.LogWarning(ex, "Chunking failed for {Url}, skipping page", page.Url);
            progress.IncrementErrorCount();
        }
    }

    #endregion

    #region Embed stage

    private async Task RunEmbedStageAsync(ChannelReader<DocChunk[]> input,
                                          ChannelWriter<DocChunk[]> output,
                                          ScrapeJobRecord progress,
                                          Action<ScrapeJobRecord>? onProgress,
                                          CancellationTokenSource cts)
    {
        var batch = new List<DocChunk>();

        try
        {
            await foreach(var pageChunks in input.ReadAllAsync(cts.Token))
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
        catch(OperationCanceledException)
        {
            output.TryComplete();
            throw;
        }
        catch(Exception ex)
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

    private async Task EmbedAndForwardBatchAsync(List<DocChunk> batch,
                                                 ChannelWriter<DocChunk[]> output,
                                                 ScrapeJobRecord progress,
                                                 Action<ScrapeJobRecord>? onProgress,
                                                 CancellationToken ct)
    {
        try
        {
            var texts = batch
                        .Select(c =>
                                    TruncateForEmbedding($"[{c.Category}] [{c.LibraryId}] [{c.PageTitle}]\n{c.Content}",
                                                         MaxEmbedChars
                                                        )
                               )
                        .ToList();

            float[][] embeddings = await EmbedWithRetryAsync(texts, ct);

            var embeddedChunks = new DocChunk[batch.Count];
            for(var i = 0; i < batch.Count; i++)
                embeddedChunks[i] = batch[i] with { Embedding = embeddings[i] };

            // Upsert to MongoDB (supports resume â€” no duplicates on re-run)
            await mChunkRepository.UpsertChunksAsync(embeddedChunks, ct);
            progress.ChunksEmbedded += embeddedChunks.Length;
            onProgress?.Invoke(progress);

            await output.WriteAsync(embeddedChunks, ct);

            mLogger.LogDebug("Embedded and stored batch of {Count} chunks", embeddedChunks.Length);
        }
        catch(Exception ex) when(ex is not OperationCanceledException)
        {
            mLogger.LogWarning(ex, "Embedding failed for batch of {Count} chunks, skipping", batch.Count);
            progress.IncrementErrorCount();
        }
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
            mLogger.LogWarning(ex, "Embedding failed, retrying once");
            result = await mEmbeddingProvider.EmbedAsync(texts, ct);
        }

        return result;
    }

    #endregion

    #region Index stage

    private async Task RunIndexStageAsync(string? profile,
                                          ScrapeJob job,
                                          ChannelReader<DocChunk[]> input,
                                          ScrapeJobRecord progress,
                                          Action<ScrapeJobRecord>? onProgress,
                                          CancellationTokenSource cts)
    {
        var pendingChunks = new List<DocChunk>();
        var indexedPageUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            await foreach(var embeddedChunks in input.ReadAllAsync(cts.Token))
            {
                pendingChunks.AddRange(embeddedChunks);
                progress.ChunksCompleted += embeddedChunks.Length;

                // Track unique page URLs that have been indexed
                foreach(var chunk in embeddedChunks)
                    indexedPageUrls.Add(chunk.PageUrl);

                if (pendingChunks.Count >= IndexRebuildInterval)
                {
                    await RebuildIndexAsync(profile, job, pendingChunks, cts.Token);
                    pendingChunks.Clear();
                    progress.PagesCompleted = indexedPageUrls.Count;
                    onProgress?.Invoke(progress);
                }
            }

            // Final rebuild with remaining chunks
            if (pendingChunks.Count > 0)
            {
                await RebuildIndexAsync(profile, job, pendingChunks, cts.Token);
                progress.PagesCompleted = indexedPageUrls.Count;
                onProgress?.Invoke(progress);
            }
        }
        catch(OperationCanceledException)
        {
            throw;
        }
        catch(Exception ex)
        {
            mLogger.LogError(ex, "Index stage fatal error");
            await cts.CancelAsync();
            throw;
        }
    }

    private async Task RebuildIndexAsync(string? profile,
                                         ScrapeJob job,
                                         List<DocChunk> chunks,
                                         CancellationToken ct)
    {
        try
        {
            await mVectorSearch.IndexChunksAsync(profile, job.LibraryId, job.Version, chunks, ct);
            mLogger.LogInformation("Rebuilt vector index with {Count} new chunks", chunks.Count);
        }
        catch(Exception ex)
        {
            mLogger.LogWarning(ex, "Vector index rebuild failed, will retry on next batch");
        }
    }

    #endregion
}
