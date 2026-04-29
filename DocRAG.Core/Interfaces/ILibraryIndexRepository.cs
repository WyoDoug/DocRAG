// ILibraryIndexRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Models;

#endregion

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace DocRAG.Core.Interfaces;

/// <summary>
///     Data access for per-(library, version) auxiliary indexes:
///     BM25 inverted index, code-fence symbol set, and the build manifest
///     used to decide whether reclassification is needed during rescrub.
/// </summary>
public interface ILibraryIndexRepository
{
    /// <summary>
    ///     Get the index bundle for a (library, version), or null if not built yet.
    /// </summary>
    Task<LibraryIndex?> GetAsync(string libraryId, string version, CancellationToken ct = default);

    /// <summary>
    ///     Create or replace the index bundle. Idempotent.
    /// </summary>
    Task UpsertAsync(LibraryIndex index, CancellationToken ct = default);

    /// <summary>
    ///     Delete the index bundle. Used by tests and force-rebuild paths.
    ///     Returns the count of deleted documents.
    /// </summary>
    Task<long> DeleteAsync(string libraryId, string version, CancellationToken ct = default);
}
