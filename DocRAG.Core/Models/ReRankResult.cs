// ReRankResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace DocRAG.Core.Models;

/// <summary>
///     A single result from the re-ranking pass.
/// </summary>
public record ReRankResult
{
    /// <summary>
    ///     The chunk being scored.
    /// </summary>
    public required DocChunk Chunk { get; init; }

    /// <summary>
    ///     Relevance score from the cross-encoder (higher = more relevant).
    /// </summary>
    public required float RelevanceScore { get; init; }
}
