// RejectedToken.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Core.Enums;

#endregion

namespace SaddleRAG.Ingestion.Symbols;

/// <summary>
///     A single token the extractor rejected. The rescrub pass aggregates
///     these per (library, version) into ExcludedSymbol records — the
///     extractor itself does not capture sample sentences (it only sees a
///     single chunk's content; sampling needs the full corpus).
/// </summary>
public record RejectedToken
{
    /// <summary>
    ///     Exact token text (case preserved).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     Why the extractor rejected this token.
    /// </summary>
    public required SymbolRejectionReason Reason { get; init; }
}
