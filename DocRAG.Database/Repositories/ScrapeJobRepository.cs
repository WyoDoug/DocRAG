// ScrapeJobRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

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
}
