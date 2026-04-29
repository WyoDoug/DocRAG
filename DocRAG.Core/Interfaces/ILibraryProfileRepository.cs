// ILibraryProfileRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Models;

#endregion

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace DocRAG.Core.Interfaces;

/// <summary>
///     Data access for per-(library, version) reconnaissance profiles.
///     A profile is the LLM-produced characterization of a docs site
///     (languages, casing, separators, likely symbols) used to drive
///     identifier-aware extraction and ranking heuristics.
/// </summary>
public interface ILibraryProfileRepository
{
    /// <summary>
    ///     Get the profile for a (library, version), or null if none cached.
    /// </summary>
    Task<LibraryProfile?> GetAsync(string libraryId, string version, CancellationToken ct = default);

    /// <summary>
    ///     Create or replace a profile. Idempotent.
    /// </summary>
    Task UpsertAsync(LibraryProfile profile, CancellationToken ct = default);

    /// <summary>
    ///     Delete a profile. Used by tests and force-refresh paths.
    ///     Returns the count of deleted documents.
    /// </summary>
    Task<long> DeleteAsync(string libraryId, string version, CancellationToken ct = default);

    /// <summary>
    ///     List every cached profile. Used by diagnostics.
    /// </summary>
    Task<IReadOnlyList<LibraryProfile>> ListAllAsync(CancellationToken ct = default);
}
