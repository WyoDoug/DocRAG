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
}
