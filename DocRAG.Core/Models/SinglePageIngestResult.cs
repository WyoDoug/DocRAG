// SinglePageIngestResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace DocRAG.Core.Models;

/// <summary>
///     Outcome of <c>add_page</c> / <c>IngestSinglePageAsync</c>. Returned
///     verbatim through the MCP tool so callers can see whether the page
///     was indexed, failed to fetch, or produced no chunks worth storing.
/// </summary>
public sealed record SinglePageIngestResult
{
    /// <summary>
    ///     Indexed | Empty | Failed.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    ///     URL the caller asked to add.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    ///     Library identifier the page was added to.
    /// </summary>
    public required string Library { get; init; }

    /// <summary>
    ///     Version the page was added to.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    ///     Number of chunks produced and persisted. Populated only when
    ///     <see cref="Status"/> is <c>Indexed</c>.
    /// </summary>
    public int ChunksAdded { get; init; }

    /// <summary>
    ///     Resolved doc category, e.g. <c>HowTo</c>, <c>ApiReference</c>.
    ///     Populated only when <see cref="Status"/> is <c>Indexed</c>.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    ///     Human-readable reason. Populated when <see cref="Status"/> is
    ///     <c>Failed</c> or <c>Empty</c> to explain what went wrong.
    /// </summary>
    public string? Reason { get; init; }
}
