// IChunkRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;
using DocRAG.Core.Models;

#endregion

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace DocRAG.Core.Interfaces;

/// <summary>
///     Data access for documentation chunks. Vector search is handled
///     separately by IVectorSearchProvider.
/// </summary>
public interface IChunkRepository
{
    /// <summary>
    ///     Store a batch of chunks.
    /// </summary>
    Task InsertChunksAsync(IReadOnlyList<DocChunk> chunks, CancellationToken ct = default);

    /// <summary>
    ///     Upsert a batch of chunks (insert or replace by Id).
    ///     Used by the streaming embed stage to support resume without duplicates.
    /// </summary>
    Task UpsertChunksAsync(IReadOnlyList<DocChunk> chunks, CancellationToken ct = default);

    /// <summary>
    ///     Delete all chunks for a library version (used before re-chunking).
    /// </summary>
    Task DeleteChunksAsync(string libraryId, string version, CancellationToken ct = default);

    /// <summary>
    ///     Get all chunks for a library version (for indexing into vector search).
    /// </summary>
    Task<IReadOnlyList<DocChunk>> GetChunksAsync(string libraryId, string version, CancellationToken ct = default);

    /// <summary>
    ///     Get chunk count for a library version.
    /// </summary>
    Task<int> GetChunkCountAsync(string libraryId, string version, CancellationToken ct = default);

    /// <summary>
    ///     Find chunks by qualified class/member name (exact or prefix match).
    /// </summary>
    Task<IReadOnlyList<DocChunk>> FindByQualifiedNameAsync(string libraryId,
                                                           string version,
                                                           string qualifiedName,
                                                           CancellationToken ct = default);

    /// <summary>
    ///     Get all distinct qualified names for a library version (for list_classes).
    ///     Returns Symbols[] entries with Kind == Type for v2+ chunks; falls back to
    ///     legacy QualifiedName for v1 chunks until they are rescrubbed.
    /// </summary>
    Task<IReadOnlyList<string>> GetQualifiedNamesAsync(string libraryId,
                                                       string version,
                                                       string? filter = null,
                                                       CancellationToken ct = default);

    /// <summary>
    ///     Get distinct symbol names of a specific kind across the library version.
    ///     Used by the per-kind list tools (list_enums, list_functions,
    ///     list_parameters). Reads only v2+ chunks; legacy chunks have no
    ///     SymbolKind classification and are skipped.
    /// </summary>
    Task<IReadOnlyList<string>> GetSymbolsAsync(string libraryId,
                                                string version,
                                                SymbolKind kind,
                                                string? filter = null,
                                                CancellationToken ct = default);

    /// <summary>
    ///     Bulk update the Category field for all chunks belonging to a given page URL.
    ///     Used by the reclassify workflow when an LLM corrects a page's category
    ///     after initial ingestion.
    /// </summary>
    Task<long> UpdateCategoryByPageUrlAsync(string libraryId,
                                            string version,
                                            string pageUrl,
                                            DocCategory newCategory,
                                            CancellationToken ct = default);

    /// <summary>
    ///     Returns true when at least one chunk in (libraryId, version) has
    ///     ParserVersion strictly less than the supplied threshold. Used by
    ///     start_ingest to detect the STALE state — the library was indexed
    ///     under an older extractor and should be rescrubbed.
    /// </summary>
    Task<bool> HasStaleChunksAsync(string libraryId,
                                   string version,
                                   int currentParserVersion,
                                   CancellationToken ct = default);
}
