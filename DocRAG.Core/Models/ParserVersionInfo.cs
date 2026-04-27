// ParserVersionInfo.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace DocRAG.Core.Models;

/// <summary>
///     Codebase-wide knowledge of the current symbol-extractor version.
///     Bump <see cref="Current" /> whenever the extractor's keep/reject
///     rules, token shapes, or SymbolKind taxonomy change. Chunks with
///     ParserVersion below this value are considered STALE by start_ingest
///     and need to be rescrubbed.
/// </summary>
public static class ParserVersionInfo
{
    /// <summary>
    ///     Current parser version emitted by the extractor in this build.
    ///     Legacy chunks default to 1; bump this when the extractor's
    ///     observable behavior changes.
    ///
    ///     v1: regex-based ExtractQualifiedName — only matched
    ///         "class|interface|struct|enum X" patterns; trailing dots
    ///         in [\w\.]+ leaked candidates like "AxisFault.".
    ///     v2: identifier-aware SymbolExtractor — IdentifierTokenizer +
    ///         keep/reject rules driven by LibraryProfile + corpus
    ///         context, emitting Symbols[] with SymbolKind.
    /// </summary>
    public const int Current = 2;
}
