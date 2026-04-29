// ScrapeJobRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using MongoDB.Driver;

#endregion

namespace DocRAG.Database.Repositories;

/// <summary>
///     MongoDB implementation of scrape job tracking.
/// </summary>
public class ScrapeJobRepository : IScrapeJobRepository
{
    public ScrapeJobRepository(DocRagDbContext context)
    {
        mContext = context;
    }

    private readonly DocRagDbContext mContext;

    private const string JobLibraryIdPath = "Job.LibraryId";
    private const string JobVersionPath = "Job.Version";

    /// <inheritdoc />
    public async Task UpsertAsync(ScrapeJobRecord job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        await mContext.ScrapeJobs.ReplaceOneAsync(j => j.Id == job.Id,
                                                  job,
                                                  new ReplaceOptions { IsUpsert = true },
                                                  ct
                                                 );
    }

    /// <inheritdoc />
    public async Task<ScrapeJobRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        var result = await mContext.ScrapeJobs
                                   .Find(j => j.Id == id)
                                   .FirstOrDefaultAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScrapeJobRecord>> ListRecentAsync(int limit = 20,
                                                                      CancellationToken ct = default)
    {
        var results = await mContext.ScrapeJobs
                                    .Find(FilterDefinition<ScrapeJobRecord>.Empty)
                                    .SortByDescending(j => j.CreatedAt)
                                    .Limit(limit)
                                    .ToListAsync(ct);
        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScrapeJobRecord>> ListRunningJobsAsync(CancellationToken ct = default)
    {
        var filter = Builders<ScrapeJobRecord>.Filter.Eq(j => j.Status, ScrapeJobStatus.Running);
        var results = await mContext.ScrapeJobs.Find(filter)
                                    .SortByDescending(j => j.CreatedAt)
                                    .ToListAsync(ct);
        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScrapeJobRecord>> ListActiveJobsAsync(string libraryId,
                                                                           string version,
                                                                           CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var filter = Builders<ScrapeJobRecord>.Filter.And(
            Builders<ScrapeJobRecord>.Filter.Eq(JobLibraryIdPath, libraryId),
            Builders<ScrapeJobRecord>.Filter.Eq(JobVersionPath, version),
            Builders<ScrapeJobRecord>.Filter.In(j => j.Status, new[] { ScrapeJobStatus.Queued, ScrapeJobStatus.Running })
        );
        var results = await mContext.ScrapeJobs.Find(filter)
                                    .SortByDescending(j => j.CreatedAt)
                                    .ToListAsync(ct);
        return results;
    }

    /// <inheritdoc />
    public async Task<ScrapeJobRecord?> GetActiveJobAsync(string libraryId,
                                                          string version,
                                                          CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var candidates = await ListActiveJobsAsync(libraryId, version, ct);
        var staleCutoff = DateTime.UtcNow - ScrapeJobThresholds.StaleRunning;
        var result = candidates.FirstOrDefault(j => !ScrapeJobThresholds.IsStaleRunning(j, staleCutoff));
        return result;
    }
}
