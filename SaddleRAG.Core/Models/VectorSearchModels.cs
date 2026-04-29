// VectorSearchModels.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Core.Enums;

#endregion

namespace SaddleRAG.Core.Models;

/// <summary>
///     Filter criteria for vector search queries.
/// </summary>
public record VectorSearchFilter
{
    /// <summary>
    ///     Database profile name. Null = default profile.
    ///     Each profile has its own isolated vector index.
    /// </summary>
    public string? Profile { get; init; }

    /// <summary>
    ///     If null, search across ALL libraries (cross-library search).
    /// </summary>
    public string? LibraryId { get; init; }

    /// <summary>
    ///     Specific version to search. Null defaults to current.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    ///     Optional category filter.
    /// </summary>
    public DocCategory? Category { get; init; }
}
