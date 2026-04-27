// RechunkOptions.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace DocRAG.Core.Models;

/// <summary>
///     Knobs for <c>rechunk_library</c>. Drives a chunker-only refresh of an
///     already-ingested library: pages stay, chunks get rebuilt, embeddings
///     are regenerated. No re-crawl, no re-classify.
/// </summary>
public class RechunkOptions
{
    /// <summary>
    ///     If true, report what would change without writing to MongoDB or
    ///     touching the vector index.
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    ///     Run the chunk-boundary audit before and after to confirm the new
    ///     chunker actually reduced mid-identifier cuts. Defaults to true.
    /// </summary>
    public bool BoundaryAudit { get; init; } = true;

    /// <summary>
    ///     Optional cap on the number of pages to process. Used for spot-checks
    ///     on large libraries.
    /// </summary>
    public int? MaxPages { get; init; }
}
