// // ScrapeJobRunner.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

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
    public async Task<string> QueueAsync(ScrapeJob job, string? profile = null, CancellationToken ct = default)
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

            await mOrchestrator.IngestAsync(jobRecord.Job,
                                            jobRecord.Profile,
                                            forceClean: jobRecord.Job.ForceClean,
                                            updatedRecord =>
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
                                            jobRecord
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

    private const string PipelineStateStarting = "Starting";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> smJobLocks =
        new ConcurrentDictionary<string, SemaphoreSlim>();
}
