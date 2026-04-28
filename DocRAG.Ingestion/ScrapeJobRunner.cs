// ScrapeJobRunner.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.Collections.Concurrent;
using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using DocRAG.Database.Repositories;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

#endregion

namespace DocRAG.Ingestion;

/// <summary>
///     Runs scrape jobs in the background, tracks status in MongoDB,
///     and reloads the per-profile vector index when ingestion completes.
/// </summary>
public class ScrapeJobRunner : IScrapeJobQueue
{
    public ScrapeJobRunner(IngestionOrchestrator orchestrator,
                           IScrapeJobRepository jobRepository,
                           IChunkRepository chunkRepository,
                           IVectorSearchProvider vectorSearch,
                           ILibraryRepository libraryRepository,
                           ILogger<ScrapeJobRunner> logger,
                           RepositoryFactory repositoryFactory,
                           IHostApplicationLifetime lifetime)
    {
        mOrchestrator = orchestrator;
        mJobRepository = jobRepository;
        mChunkRepository = chunkRepository;
        mVectorSearch = vectorSearch;
        mLibraryRepository = libraryRepository;
        mLogger = logger;
        mRepositoryFactory = repositoryFactory;
        mAppStoppingToken = lifetime.ApplicationStopping;
    }

    private readonly ConcurrentDictionary<string, CancellationTokenSource> mActiveJobCts = new();
    private readonly CancellationToken mAppStoppingToken;
    private readonly IChunkRepository mChunkRepository;
    private readonly IScrapeJobRepository mJobRepository;
    private readonly ILibraryRepository mLibraryRepository;
    private readonly ILogger<ScrapeJobRunner> mLogger;
    private readonly IngestionOrchestrator mOrchestrator;
    private readonly RepositoryFactory mRepositoryFactory;
    private readonly IVectorSearchProvider mVectorSearch;

    /// <summary>
    ///     Queue a job and kick off background execution.
    ///     Returns the job id immediately.
    /// </summary>
    public virtual async Task<string> QueueAsync(ScrapeJob job, string? profile = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var jobRecord = new ScrapeJobRecord
                            {
                                Id = Guid.NewGuid().ToString(),
                                Job = job,
                                Profile = profile,
                                Status = ScrapeJobStatus.Queued
                            };

        await mJobRepository.UpsertAsync(jobRecord, ct);

        // Fire-and-forget background execution. Errors land in the job record.
        _ = Task.Run(() => RunJobAsync(jobRecord), mAppStoppingToken);

        return jobRecord.Id;
    }

    private async Task RunJobAsync(ScrapeJobRecord jobRecord)
    {
        var lockKey = $"{jobRecord.Job.LibraryId}/{jobRecord.Job.Version}";
        var semaphore = smJobLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(initialCount: 1, maxCount: 1));

        await semaphore.WaitAsync(mAppStoppingToken);
        CancellationTokenSource? cts = null;
        try
        {
            jobRecord.Status = ScrapeJobStatus.Running;
            jobRecord.StartedAt = DateTime.UtcNow;
            jobRecord.PipelineState = PipelineStateStarting;
            await mJobRepository.UpsertAsync(jobRecord);

            mLogger.LogInformation("Running scrape job {JobId} for {LibraryId} v{Version}",
                                   jobRecord.Id,
                                   jobRecord.Job.LibraryId,
                                   jobRecord.Job.Version
                                  );

            cts = CancellationTokenSource.CreateLinkedTokenSource(mAppStoppingToken);
            mActiveJobCts[jobRecord.Id] = cts;

            await mOrchestrator.IngestAsync(jobRecord.Job,
                                            jobRecord.Profile,
                                            forceClean: jobRecord.Job.ForceClean,
                                            updatedRecord =>
                                            {
                                                bool counterIncreased =
                                                    updatedRecord.PagesQueued != jobRecord.PagesQueued ||
                                                    updatedRecord.PagesFetched != jobRecord.PagesFetched ||
                                                    updatedRecord.PagesClassified != jobRecord.PagesClassified ||
                                                    updatedRecord.ChunksGenerated != jobRecord.ChunksGenerated ||
                                                    updatedRecord.ChunksEmbedded != jobRecord.ChunksEmbedded ||
                                                    updatedRecord.ChunksCompleted != jobRecord.ChunksCompleted ||
                                                    updatedRecord.PagesCompleted != jobRecord.PagesCompleted;

                                                jobRecord.PipelineState = updatedRecord.PipelineState;
                                                jobRecord.PagesQueued = updatedRecord.PagesQueued;
                                                jobRecord.PagesFetched = updatedRecord.PagesFetched;
                                                jobRecord.PagesClassified = updatedRecord.PagesClassified;
                                                jobRecord.ChunksGenerated = updatedRecord.ChunksGenerated;
                                                jobRecord.ChunksEmbedded = updatedRecord.ChunksEmbedded;
                                                jobRecord.ChunksCompleted = updatedRecord.ChunksCompleted;
                                                jobRecord.PagesCompleted = updatedRecord.PagesCompleted;
                                                jobRecord.ErrorCount = updatedRecord.ErrorCount;

                                                if (counterIncreased)
                                                    jobRecord.LastProgressAt = DateTime.UtcNow;

                                                mJobRepository.UpsertAsync(jobRecord).GetAwaiter().GetResult();
                                            },
                                            jobRecord,
                                            cts.Token
                                           );

            // Reload the vector index for this library version so the new
            // chunks are immediately searchable via search_docs.
            await ReloadIndexForLibraryAsync(jobRecord.Profile, jobRecord.Job.LibraryId, jobRecord.Job.Version);

            jobRecord.Status = ScrapeJobStatus.Completed;
            jobRecord.PipelineState = nameof(ScrapeJobStatus.Completed);
            jobRecord.CompletedAt = DateTime.UtcNow;
            await mJobRepository.UpsertAsync(jobRecord);

            mLogger.LogInformation("Scrape job {JobId} completed successfully", jobRecord.Id);
        }
        catch(Exception) when (cts != null && cts.IsCancellationRequested)
        {
            mLogger.LogInformation("Scrape job {JobId} was cancelled", jobRecord.Id);

            // CancelAsync already wrote the Cancelled status; only update if it
            // hasn't been persisted yet (e.g. cancellation came from app shutdown).
            if (jobRecord.Status != ScrapeJobStatus.Cancelled)
            {
                jobRecord.Status = ScrapeJobStatus.Cancelled;
                jobRecord.PipelineState = nameof(ScrapeJobStatus.Cancelled);
                jobRecord.CancelledAt = DateTime.UtcNow;
                jobRecord.CompletedAt = DateTime.UtcNow;
                await mJobRepository.UpsertAsync(jobRecord);
            }
        }
        catch(Exception ex)
        {
            mLogger.LogError(ex, "Scrape job {JobId} failed", jobRecord.Id);

            jobRecord.Status = ScrapeJobStatus.Failed;
            jobRecord.ErrorMessage = ex.Message;
            jobRecord.PipelineState = nameof(ScrapeJobStatus.Failed);
            jobRecord.CompletedAt = DateTime.UtcNow;
            await mJobRepository.UpsertAsync(jobRecord);
        }
        finally
        {
            if (cts != null)
            {
                mActiveJobCts.TryRemove(jobRecord.Id, out _);
                cts.Dispose();
            }

            semaphore.Release();
        }
    }

    /// <summary>
    ///     Rebuild the in-memory vector index for a library version
    ///     from the chunks currently stored in MongoDB.
    /// </summary>
    public async Task ReloadIndexForLibraryAsync(string? profile,
                                                 string libraryId,
                                                 string version,
                                                 CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var chunks = await mChunkRepository.GetChunksAsync(libraryId, version, ct);
        var embeddedChunks = chunks.Where(c => c.Embedding != null).ToList();

        await mVectorSearch.IndexChunksAsync(profile, libraryId, version, embeddedChunks, ct);

        mLogger.LogInformation("Reloaded vector index for {Profile}/{Library} v{Version}: {Count} chunks",
                               profile ?? "(default)",
                               libraryId,
                               version,
                               embeddedChunks.Count
                              );
    }

    /// <summary>
    ///     Reload all library indices for a given profile.
    /// </summary>
    public async Task ReloadProfileAsync(string? profile, CancellationToken ct = default)
    {
        var libraryRepo = mRepositoryFactory.GetLibraryRepository(profile);
        var chunkRepo = mRepositoryFactory.GetChunkRepository(profile);

        var libraries = await libraryRepo.GetAllLibrariesAsync(ct);
        foreach(var lib in libraries)
        {
            var chunks = await chunkRepo.GetChunksAsync(lib.Id, lib.CurrentVersion, ct);
            var embeddedChunks = chunks.Where(c => c.Embedding != null).ToList();

            await mVectorSearch.IndexChunksAsync(profile, lib.Id, lib.CurrentVersion, embeddedChunks, ct);
        }

        mLogger.LogInformation("Reloaded all libraries for profile {Profile} ({Count} libraries)",
                               profile ?? "(default)",
                               libraries.Count
                              );
    }

    /// <summary>
    ///     Cancel an in-flight or orphaned scrape job. Returns a
    ///     <see cref="CancelScrapeOutcome" /> the caller can map to a user-facing message.
    ///     If an active runner exists the CTS is signalled; if the process was restarted
    ///     and the job is stranded in Running state the DB row is updated directly.
    /// </summary>
    public virtual async Task<CancelScrapeOutcome> CancelAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        var jobRepo = mRepositoryFactory.GetScrapeJobRepository(profile: null);
        var record = await jobRepo.GetAsync(jobId, ct);

        CancelScrapeOutcome result;
        switch (record)
        {
            case null:
                result = CancelScrapeOutcome.NotFound;
                break;
            case { Status: ScrapeJobStatus.Completed or ScrapeJobStatus.Failed or ScrapeJobStatus.Cancelled }:
                result = CancelScrapeOutcome.AlreadyTerminal;
                break;
            default:
                if (mActiveJobCts.TryGetValue(jobId, out var cts))
                {
                    await cts.CancelAsync();
                    result = CancelScrapeOutcome.Signalled;
                }
                else
                    result = CancelScrapeOutcome.OrphanCleanedUp;

                record.Status = ScrapeJobStatus.Cancelled;
                record.PipelineState = nameof(ScrapeJobStatus.Cancelled);
                record.CancelledAt = DateTime.UtcNow;
                record.CompletedAt = DateTime.UtcNow;
                await jobRepo.UpsertAsync(record, ct);
                break;
        }

        return result;
    }

    private const string PipelineStateStarting = "Starting";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> smJobLocks =
        new ConcurrentDictionary<string, SemaphoreSlim>();
}
