// LibraryIndex.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace DocRAG.Core.Models;

/// <summary>
///     Per-library auxiliary indexes used for hybrid retrieval and
///     identifier-aware extraction. Stored in the library_indexes
///     collection, keyed by (LibraryId, Version).
/// </summary>
public record LibraryIndex
{
    /// <summary>
    ///     Mongo document id. Format: "{LibraryId}/{Version}".
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     Library this index applies to.
    /// </summary>
    public required string LibraryId { get; init; }

    /// <summary>
    ///     Version this index applies to.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    ///     Inline metadata for BM25 scoring — DocLengths, DocumentCount,
    ///     AverageDocLength, ShardCount. The actual postings live in the
    ///     sharded <c>bm25Shards</c> collection; load via
    ///     <c>IBm25ShardRepository</c>.
    /// </summary>
    public Bm25Stats Bm25 { get; init; } = new();

    /// <summary>
    ///     Set of every identifier-shaped token appearing inside any code
    ///     fence anywhere in the library's chunks. Drives the
    ///     "appears in code fence" keep rule of the symbol extractor.
    /// </summary>
    public IReadOnlyList<string> CodeFenceSymbols { get; init; } = [];

    /// <summary>
    ///     Versioning metadata: parser/profile/classifier versions that were
    ///     current when this index was last built.
    /// </summary>
    public LibraryManifest Manifest { get; init; } = new();
}
