// ILibraryRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Models;

#endregion

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace DocRAG.Core.Interfaces;

/// <summary>
///     Data access for library and library version records.
/// </summary>
public interface ILibraryRepository
{
    /// <summary>
    ///     Get all libraries.
    /// </summary>
    Task<IReadOnlyList<LibraryRecord>> GetAllLibrariesAsync(CancellationToken ct = default);

    /// <summary>
    ///     Get a library by its unique identifier.
    /// </summary>
    Task<LibraryRecord?> GetLibraryAsync(string libraryId, CancellationToken ct = default);

    /// <summary>
    ///     Create or update a library record.
    /// </summary>
    Task UpsertLibraryAsync(LibraryRecord library, CancellationToken ct = default);

    /// <summary>
    ///     Get version metadata for a specific library version.
    /// </summary>
    Task<LibraryVersionRecord?> GetVersionAsync(string libraryId, string version, CancellationToken ct = default);

    /// <summary>
    ///     Store version metadata after a scrape completes.
    /// </summary>
    Task UpsertVersionAsync(LibraryVersionRecord versionRecord, CancellationToken ct = default);

    /// <summary>
    ///     Delete a specific version of a library. Removes the LibraryVersions row,
    ///     then either deletes the Library row (if no versions remain) or repoints
    ///     CurrentVersion to the next-most-recent version.
    /// </summary>
    Task<DeleteVersionResult> DeleteVersionAsync(string libraryId, string version, CancellationToken ct = default);

    /// <summary>
    ///     Delete a complete library and all its versions.
    ///     Iterates through all versions and calls DeleteVersionAsync for each,
    ///     then ensures the Library row is deleted.
    /// </summary>
    Task<long> DeleteAsync(string libraryId, CancellationToken ct = default);
}
