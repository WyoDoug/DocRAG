// ExcludedSymbol.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Core.Enums;

#endregion

namespace SaddleRAG.Core.Models;

/// <summary>
///     Persistable record describing a token the symbol extractor rejected.
///     Stored in the library_excluded_symbols collection keyed by
///     (LibraryId, Version, Name). Carries the reason plus a few sample
///     sentences so a calling LLM can decide whether the rejection was
///     correct.
/// </summary>
public record ExcludedSymbol
{
    /// <summary>
    ///     Mongo document id. Format: "{LibraryId}/{Version}/{Name}".
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     Library identifier the rejection applies to.
    /// </summary>
    public required string LibraryId { get; init; }

    /// <summary>
    ///     Version of the library the rejection applies to.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    ///     Exact token text (case preserved).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     Why the extractor rejected this token.
    /// </summary>
    public required SymbolRejectionReason Reason { get; init; }

    /// <summary>
    ///     Up to three corpus snippets containing this token, drawn from
    ///     different thirds of the chunk stream when possible. Each entry
    ///     is at most 200 characters.
    /// </summary>
    public required IReadOnlyList<string> SampleSentences { get; init; }

    /// <summary>
    ///     Total number of chunks in which the token appeared.
    /// </summary>
    public required int ChunkCount { get; init; }

    /// <summary>
    ///     UTC time the rejection record was captured (last rescrub).
    /// </summary>
    public DateTime CapturedUtc { get; init; }

    /// <summary>
    ///     Compose the document id used as the primary key.
    /// </summary>
    public static string MakeId(string libraryId, string version, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(name);
        return $"{libraryId}/{version}/{name}";
    }
}
