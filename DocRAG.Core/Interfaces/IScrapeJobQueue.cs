// IScrapeJobQueue.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Models;

#endregion

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace DocRAG.Core.Interfaces;

/// <summary>
///     Enqueues scrape jobs for background processing.
/// </summary>
public interface IScrapeJobQueue
{
    /// <summary>
    ///     Queue a scrape job and return its identifier immediately.
    /// </summary>
    Task<string> QueueAsync(ScrapeJob job, string? profile = null, CancellationToken ct = default);
}
