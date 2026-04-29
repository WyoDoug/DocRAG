// LibraryVersionRecord.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace DocRAG.Core.Models;

/// <summary>
///     Metadata for a specific version of a library scrape.
/// </summary>
public record LibraryVersionRecord
{
    /// <summary>
    ///     Unique identifier. Example: "infragistics-wpf-25.2"
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     Parent library identifier.
    /// </summary>
    public required string LibraryId { get; init; }

    /// <summary>
    ///     Version string for this scrape.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    ///     When this version was scraped.
    /// </summary>
    public required DateTime ScrapedAt { get; init; }

    /// <summary>
    ///     Total pages fetched.
    /// </summary>
    public required int PageCount { get; init; }

    /// <summary>
    ///     Total chunks generated.
    /// </summary>
    public required int ChunkCount { get; init; }

    /// <summary>
    ///     Embedding provider used for this version's chunks.
    ///     Queries must use the same provider for vector similarity.
    /// </summary>
    public required string EmbeddingProviderId { get; init; }

    /// <summary>
    ///     Specific model name used for embeddings.
    ///     Example: "nomic-embed-text" â€” used to ensure the same
    ///     model is loaded at query time.
    /// </summary>
    public required string EmbeddingModelName { get; init; }

    /// <summary>
    ///     Dimensionality of the stored embeddings.
    /// </summary>
    public required int EmbeddingDimensions { get; init; }

    /// <summary>
    ///     Previous version this was compared against, if any.
    /// </summary>
    public string? PreviousVersion { get; init; }

    /// <summary>
    ///     Percentage of chunks with boundary issues detected during extraction.
    ///     Range: 0.0 to 100.0. Default 0.
    /// </summary>
    public double BoundaryIssuePct { get; set; }

    /// <summary>
    ///     Whether this library version is flagged as suspect by the detector pipeline.
    /// </summary>
    public bool Suspect { get; set; }

    /// <summary>
    ///     Reasons why this library version is marked suspect.
    /// </summary>
    public IReadOnlyList<string> SuspectReasons { get; set; } = Array.Empty<string>();

    /// <summary>
    ///     When the suspect status was last evaluated.
    /// </summary>
    public DateTime? LastSuspectEvaluatedAt { get; set; }
}
