// IScrapeJobRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Models;

#endregion

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace DocRAG.Core.Interfaces;

/// <summary>
///     Data access for scrape job tracking records.
/// </summary>
public interface IScrapeJobRepository
{
    /// <summary>
    ///     Create or update a job record.
    /// </summary>
    Task UpsertAsync(ScrapeJobRecord job, CancellationToken ct = default);

    /// <summary>
    ///     Get a job by id.
    /// </summary>
    Task<ScrapeJobRecord?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>
    ///     List recent jobs (most recent first).
    /// </summary>
    Task<IReadOnlyList<ScrapeJobRecord>> ListRecentAsync(int limit = 20, CancellationToken ct = default);

    /// <summary>
    ///     List every job currently in <see cref="DocRAG.Core.Enums.ScrapeJobStatus.Running"/>
    ///     across all libraries, sorted by CreatedAt descending. Used by
    ///     get_dashboard_index to surface orphan Running jobs that fall
    ///     outside the recent-jobs window.
    /// </summary>
    Task<IReadOnlyList<ScrapeJobRecord>> ListRunningJobsAsync(CancellationToken ct = default);

    /// <summary>
    ///     List every Queued or Running job for (libraryId, version),
    ///     sorted by CreatedAt descending. Used by submit_url_correction
    ///     to cancel parallel in-flight work on the same library/version
    ///     before re-queuing at a corrected URL.
    /// </summary>
    Task<IReadOnlyList<ScrapeJobRecord>> ListActiveJobsAsync(string libraryId,
                                                             string version,
                                                             CancellationToken ct = default);

    /// <summary>
    ///     Return the most-recently-created Queued or non-stale Running
    ///     job for (libraryId, version), or null if none exists. Stale
    ///     Running jobs (orphans whose effective progress timestamp is
    ///     older than <see cref="ScrapeJobThresholds.StaleRunning"/>) are
    ///     skipped so the start_ingest state machine can move forward
    ///     instead of getting wedged on a dead runner.
    /// </summary>
    Task<ScrapeJobRecord?> GetActiveJobAsync(string libraryId, string version, CancellationToken ct = default);
}
