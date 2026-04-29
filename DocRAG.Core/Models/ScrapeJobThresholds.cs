// ScrapeJobThresholds.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;

#endregion

namespace DocRAG.Core.Models;

/// <summary>
///     Shared thresholds and helpers for scrape-job lifecycle decisions.
///     A Running job whose effective progress timestamp (LastProgressAt,
///     falling back to CreatedAt when no progress has been recorded) is
///     older than <see cref="StaleRunning" /> is treated as an orphan: the
///     dashboard surfaces it with a Stale flag, and start_ingest's
///     active-job query ignores it so the state machine can move forward.
/// </summary>
public static class ScrapeJobThresholds
{
    /// <summary>
    ///     A Running job with no forward motion in this window is considered
    ///     a stale orphan. Forward motion = any update to PagesFetched,
    ///     PagesCompleted, ChunksGenerated, or ChunksEmbedded, which the
    ///     pipeline records by stamping LastProgressAt.
    /// </summary>
    public static TimeSpan StaleRunning { get; } = TimeSpan.FromHours(StaleRunningHours);

    private const int StaleRunningHours = 4;

    /// <summary>
    ///     True when <paramref name="job"/> is in <see cref="ScrapeJobStatus.Running"/>
    ///     and has had no forward motion since <paramref name="staleCutoff"/>.
    ///     Jobs that have never recorded progress fall back to CreatedAt so
    ///     a Running row that died before its first heartbeat is still
    ///     classified as stale.
    /// </summary>
    public static bool IsStaleRunning(ScrapeJobRecord job, DateTime staleCutoff)
    {
        ArgumentNullException.ThrowIfNull(job);

        bool res = false;
        if (job.Status == ScrapeJobStatus.Running)
        {
            var effective = job.LastProgressAt ?? job.CreatedAt;
            res = effective < staleCutoff;
        }
        return res;
    }
}
