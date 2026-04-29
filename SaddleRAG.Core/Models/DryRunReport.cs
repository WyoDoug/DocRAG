// DryRunReport.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Result of a dry-run crawl. Shows what a real ingestion
///     would have stored, without actually writing anything.
/// </summary>
public record DryRunReport
{
    /// <summary>
    ///     Total pages that would be fetched and stored.
    /// </summary>
    public required int TotalPages { get; init; }

    /// <summary>
    ///     Pages that fall within the root scope (same host + path prefix).
    /// </summary>
    public required int InScopePages { get; init; }

    /// <summary>
    ///     Pages that are out of scope but within the depth limit.
    /// </summary>
    public required int OutOfScopePages { get; init; }

    /// <summary>
    ///     Pages skipped because the out-of-scope depth limit was exceeded.
    /// </summary>
    public required int DepthLimitedSkips { get; init; }

    /// <summary>
    ///     Pages skipped due to URL allow/exclude pattern mismatch.
    /// </summary>
    public required int FilteredSkips { get; init; }

    /// <summary>
    ///     Pages that returned an error during fetch.
    /// </summary>
    public required int FetchErrors { get; init; }

    /// <summary>
    ///     Distribution of out-of-scope depth values.
    ///     Key = depth, Value = number of pages at that depth.
    /// </summary>
    public required IReadOnlyDictionary<int, int> DepthDistribution { get; init; }

    /// <summary>
    ///     Pages grouped by host.
    /// </summary>
    public required IReadOnlyDictionary<string, int> PagesByHost { get; init; }

    /// <summary>
    ///     GitHub repositories that would be cloned (owner/repo strings).
    /// </summary>
    public required IReadOnlyList<string> GitHubReposToClone { get; init; }

    /// <summary>
    ///     Sample of URLs visited (first N for inspection).
    /// </summary>
    public required IReadOnlyList<DryRunPageEntry> SamplePages { get; init; }

    /// <summary>
    ///     Details of every page that failed to fetch, with error message
    ///     and HTTP status (or 0 if no response).
    /// </summary>
    public required IReadOnlyList<DryRunFetchError> Errors { get; init; }

    /// <summary>
    ///     How long the dry-run took.
    /// </summary>
    public required TimeSpan ElapsedTime { get; init; }

    /// <summary>
    ///     Hit the MaxPages safety limit before finishing.
    /// </summary>
    public required bool HitMaxPagesLimit { get; init; }

    /// <summary>
    ///     Pages that were queued but not fetched when the crawl ended
    ///     (either because MaxPages was hit or the run was cancelled).
    ///     High value = crawler found lots more to do but got cut off.
    /// </summary>
    public required int PagesRemainingInQueue { get; init; }

    /// <summary>
    ///     First N URLs that were in the queue when the run ended.
    ///     Useful for verifying allow-list patterns are doing what you expect.
    /// </summary>
    public required IReadOnlyList<string> SamplePendingUrls { get; init; }
}
