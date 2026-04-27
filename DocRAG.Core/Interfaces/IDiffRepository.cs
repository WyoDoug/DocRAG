// IDiffRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Models;

#endregion

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace DocRAG.Core.Interfaces;

/// <summary>
///     Data access for version diff records.
/// </summary>
public interface IDiffRepository
{
    /// <summary>
    ///     Store a version diff record.
    /// </summary>
    Task UpsertDiffAsync(VersionDiffRecord diff, CancellationToken ct = default);

    /// <summary>
    ///     Get a diff between two specific versions.
    /// </summary>
    Task<VersionDiffRecord?> GetDiffAsync(string libraryId,
                                          string fromVersion,
                                          string toVersion,
                                          CancellationToken ct = default);
}
