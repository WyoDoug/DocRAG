// // RechunkResult.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

namespace DocRAG.Core.Models;

/// <summary>
///     Output of <c>rechunk_library</c>. The core diagnostic is the
///     before/after BoundaryIssues delta — that's what tells the caller
///     whether the chunker change actually helped.
/// </summary>
public record RechunkResult
{
    public required string LibraryId { get; init; }
    public required string Version { get; init; }

    public int PagesProcessed { get; init; }
    public int OldChunkCount { get; init; }
    public int NewChunkCount { get; init; }
    public int BoundaryIssuesBefore { get; init; }
    public int BoundaryIssuesAfter { get; init; }
    public int ChunksEmbedded { get; init; }
    public bool DryRun { get; init; }
    public string? Message { get; init; }

    /// <summary>
    ///     Up to <c>BoundaryIssueSampleLimit</c> remaining chunker cuts in
    ///     the new chunks, surfaced for diagnosis. Empty when the new
    ///     chunker resolved every cut in the corpus.
    /// </summary>
    public IReadOnlyList<BoundaryIssue> BoundaryIssueSamples { get; init; } = [];
}
