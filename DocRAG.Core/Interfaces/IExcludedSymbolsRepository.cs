// IExcludedSymbolsRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;
using DocRAG.Core.Models;

#endregion

namespace DocRAG.Core.Interfaces;

/// <summary>
///     Persistence surface for per-(library, version) symbol-extractor
///     rejections. Populated by RescrubService at the end of each rescrub;
///     consumed by the SymbolManagement MCP tools.
/// </summary>
public interface IExcludedSymbolsRepository
{
    /// <summary>
    ///     List rejections for (libraryId, version), optionally filtered by
    ///     reason. Returns the most-prevalent rejections first (sort by
    ///     ChunkCount descending) so the LLM sees the loudest noise first.
    /// </summary>
    Task<IReadOnlyList<ExcludedSymbol>> ListAsync(string libraryId,
                                                   string version,
                                                   SymbolRejectionReason? reason,
                                                   int limit,
                                                   CancellationToken ct = default);

    /// <summary>
    ///     Insert or update each entry by Id. Existing entries with the same
    ///     Id are replaced.
    /// </summary>
    Task UpsertManyAsync(IEnumerable<ExcludedSymbol> entries, CancellationToken ct = default);

    /// <summary>
    ///     Remove rejections for (libraryId, version) whose Name matches any
    ///     entry in names (case-insensitive — aligns with the per-library
    ///     Stoplist contract that treats "foo" / "Foo" / "FOO" as equivalent).
    ///     Idempotent — names not present are silently ignored.
    /// </summary>
    Task RemoveAsync(string libraryId,
                     string version,
                     IEnumerable<string> names,
                     CancellationToken ct = default);

    /// <summary>
    ///     Wipe all rejections for (libraryId, version). Called at the start
    ///     of each rescrub so we never accumulate stale rows.
    /// </summary>
    Task DeleteAllForLibraryAsync(string libraryId, string version, CancellationToken ct = default);

    /// <summary>
    ///     Total count of rejections for (libraryId, version). Used by the
    ///     list_excluded_symbols tool's Returned/TotalExcluded headers.
    /// </summary>
    Task<int> CountAsync(string libraryId, string version, CancellationToken ct = default);
}
