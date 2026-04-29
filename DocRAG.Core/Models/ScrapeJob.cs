// ScrapeJob.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;

#endregion

namespace DocRAG.Core.Models;

/// <summary>
///     Configuration for a documentation library scrape operation.
/// </summary>
public record ScrapeJob
{
    /// <summary>
    ///     Root URL to begin crawling from.
    /// </summary>
    public required string RootUrl { get; init; }

    /// <summary>
    ///     Human-readable hint about what this library is.
    ///     Used to seed the classifier.
    /// </summary>
    public required string LibraryHint { get; init; }

    /// <summary>
    ///     Unique identifier for this library in storage.
    /// </summary>
    public required string LibraryId { get; init; }

    /// <summary>
    ///     Version string for this scrape.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    ///     URL patterns to stay within during crawl.
    /// </summary>
    public required IReadOnlyList<string> AllowedUrlPatterns { get; init; }

    /// <summary>
    ///     URL patterns to explicitly exclude.
    /// </summary>
    public IReadOnlyList<string> ExcludedUrlPatterns { get; init; } = [];

    /// <summary>
    ///     URL pattern hints for classification.
    ///     Maps regex patterns to document categories.
    /// </summary>
    public IReadOnlyDictionary<string, DocCategory> UrlClassificationHints { get; init; } =
        new Dictionary<string, DocCategory>();

    /// <summary>
    ///     Authentication configuration for sites requiring login.
    ///     Null means no authentication needed.
    /// </summary>
    public ScrapeAuthentication? Authentication { get; init; }

    /// <summary>
    ///     Content extraction strategy override.
    ///     Null means use auto-detection.
    /// </summary>
    public string? ContentExtractorId { get; init; }

    /// <summary>
    ///     Delay in milliseconds between page fetches.
    /// </summary>
    public int FetchDelayMs { get; init; } = DefaultFetchDelayMs;

    /// <summary>
    ///     Maximum number of pages to crawl. Safety limit. 0 = no limit.
    /// </summary>
    public int MaxPages { get; init; }

    /// <summary>
    ///     Maximum link-following depth for pages WITHIN the root scope
    ///     (same host and path prefix). 0 = no limit.
    /// </summary>
    public int InScopeDepth { get; init; } = 10;

    /// <summary>
    ///     Maximum link-following depth for pages on the SAME HOST
    ///     but outside the root path prefix.
    /// </summary>
    public int SameHostDepth { get; init; } = 5;

    /// <summary>
    ///     Maximum link-following depth for pages on a DIFFERENT HOST entirely.
    ///     Set to 0 to disable off-site crawling.
    /// </summary>
    public int OffSiteDepth { get; init; } = 1;

    /// <summary>
    ///     If true, the orchestrator clears existing chunks for
    ///     (LibraryId, Version) before starting and skips resume-mode
    ///     URL deduplication so every page is re-fetched. Used by
    ///     <c>scrape_docs force=true</c> to drive a true re-ingest after
    ///     the source docs change. Default false — most callers want
    ///     resume semantics.
    /// </summary>
    public bool ForceClean { get; init; }

    /// <summary>
    ///     Default per-page fetch delay. Zero means "no fixed delay" — pacing is
    ///     handled adaptively by <c>HostRateLimiter</c> based on per-host response
    ///     status (slows down on 429/503, speeds up on sustained success). A
    ///     non-zero value is still honored as an extra floor delay if a caller
    ///     wants to force pacing on top of the limiter.
    /// </summary>
    public const int DefaultFetchDelayMs = 0;
}
