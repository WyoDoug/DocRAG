// IVectorSearchProvider.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Core.Models;

#endregion

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     Abstraction for vector similarity search.
///     Implementations: in-memory HNSW (local dev), MongoDB Atlas (production).
///     Indices are isolated per database profile to support multi-user scenarios.
/// </summary>
public interface IVectorSearchProvider
{
    /// <summary>
    ///     Create or update the vector index for a library version's chunks.
    /// </summary>
    /// <param name="profile">Database profile name. Null = default profile.</param>
    Task IndexChunksAsync(string? profile,
                          string libraryId,
                          string version,
                          IReadOnlyList<DocChunk> chunks,
                          CancellationToken ct = default);

    /// <summary>
    ///     Search for chunks similar to the query embedding.
    ///     The filter's Profile field selects which profile's index to search.
    /// </summary>
    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(float[] queryEmbedding,
                                                        VectorSearchFilter filter,
                                                        int maxResults = 5,
                                                        CancellationToken ct = default);
}
