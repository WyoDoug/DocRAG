// Bm25Posting.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace DocRAG.Core.Models;

/// <summary>
///     A single posting in the BM25 inverted index — one chunk's term-frequency
///     contribution for a given term.
/// </summary>
public record Bm25Posting
{
    /// <summary>
    ///     Chunk id this posting belongs to.
    /// </summary>
    public required string ChunkId { get; init; }

    /// <summary>
    ///     How many times the term occurs in this chunk's content.
    /// </summary>
    public required int TermFrequency { get; init; }
}
