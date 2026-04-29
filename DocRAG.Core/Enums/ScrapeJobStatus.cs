// ScrapeJobStatus.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace DocRAG.Core.Enums;

/// <summary>
///     Status of an in-progress or completed scrape job.
/// </summary>
public enum ScrapeJobStatus
{
    /// <summary>
    ///     Job created but not yet started.
    /// </summary>
    Queued,

    /// <summary>
    ///     Job is currently running.
    /// </summary>
    Running,

    /// <summary>
    ///     Job finished successfully.
    /// </summary>
    Completed,

    /// <summary>
    ///     Job stopped due to an error.
    /// </summary>
    Failed,

    /// <summary>
    ///     Job was cancelled by the user.
    /// </summary>
    Cancelled = 4
}
