// BoundaryIssue.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace DocRAG.Core.Models;

/// <summary>
///     One concrete chunker boundary problem found by ChunkBoundaryAudit.
///     Surfaced so callers can investigate which chunks were cut and what
///     the corpus-confirmed dotted identifier was — useful when chasing the
///     last few residual cuts after a chunker fix.
/// </summary>
public record BoundaryIssue
{
    /// <summary>Chunk that ends with a partial dotted identifier.</summary>
    public required string PrevChunkId { get; init; }

    /// <summary>Following chunk in the same page that begins with the leaf.</summary>
    public required string NextChunkId { get; init; }

    /// <summary>Page URL the cut chunks belong to.</summary>
    public required string PageUrl { get; init; }

    /// <summary>The joined dotted identifier (e.g. "AxisFault.Disabled").</summary>
    public required string JoinedIdentifier { get; init; }

    /// <summary>Last ~80 chars of the previous chunk for context.</summary>
    public required string PrevTail { get; init; }

    /// <summary>First ~80 chars of the next chunk for context.</summary>
    public required string NextHead { get; init; }
}
