// ScrapeJobRecord.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Core.Enums;

#endregion

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
    public string PipelineState { get; set; } = nameof(ScrapeJobStatus.Queued);

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
    ///     Non-fatal error count across all stages. The crawl stage runs up to
    ///     <c>MaxParallelWorkers</c> concurrent fetchers, so increments must
    ///     route through <see cref="IncrementErrorCount"/> for atomicity.
    /// </summary>
    public int ErrorCount
    {
        get => Volatile.Read(ref mErrorCount);
        set => Volatile.Write(ref mErrorCount, value);
    }

    /// <summary>
    ///     Atomically bump <see cref="ErrorCount"/> by 1 and return the new value.
    /// </summary>
    public int IncrementErrorCount() => Interlocked.Increment(ref mErrorCount);

    private int mErrorCount;

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

    /// <summary>
    ///     When progress was last recorded.
    /// </summary>
    public DateTime? LastProgressAt { get; set; }

    /// <summary>
    ///     When the job was cancelled, if applicable.
    /// </summary>
    public DateTime? CancelledAt { get; set; }
}
