// IReRanker.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Models;

#endregion

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace DocRAG.Core.Interfaces;

/// <summary>
///     Cross-encoder re-ranking after initial vector search retrieval.
///     Improves precision by jointly encoding query + chunk for relevance scoring.
/// </summary>
public interface IReRanker
{
    /// <summary>
    ///     Re-rank candidate chunks by relevance to the query.
    ///     Returns candidates in order of decreasing relevance.
    /// </summary>
    Task<IReadOnlyList<ReRankResult>> ReRankAsync(string query,
                                                  IReadOnlyList<DocChunk> candidates,
                                                  int maxResults,
                                                  CancellationToken ct = default);
}
