// // ParserVersionInfo.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

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
    /// </summary>
    public const int Current = 1;
}
