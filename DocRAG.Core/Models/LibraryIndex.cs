// // LibraryIndex.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

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
    ///     BM25 inverted index for keyword scoring.
    /// </summary>
    public Bm25Index Bm25 { get; init; } = new();

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
