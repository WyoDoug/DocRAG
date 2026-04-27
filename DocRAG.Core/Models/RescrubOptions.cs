// RescrubOptions.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace DocRAG.Core.Models;

/// <summary>
///     Caller-supplied options for rescrub_library. Defaults match the
///     "common case": re-extract symbols, auto-decide whether to
///     reclassify based on manifest history, scan chunk boundaries,
///     rebuild library_indexes.
/// </summary>
public record RescrubOptions
{
    /// <summary>
    ///     Always true today; reserved so future tools can disable
    ///     extraction if some other mode is added.
    /// </summary>
    public bool ReExtract { get; init; } = true;

    /// <summary>
    ///     Null = auto-detect from LibraryManifest (profile change or
    ///     classifier version change → reclassify; parser-only bump →
    ///     skip). Explicit true/false overrides auto-detect.
    /// </summary>
    public bool? ReClassify { get; init; }

    /// <summary>
    ///     When true, the tool reports what would change without writing.
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    ///     When true, the pre-flight scan looks for chunks with trailing-
    ///     period content or other split-mid-identifier signs.
    /// </summary>
    public bool BoundaryAudit { get; init; } = true;

    /// <summary>
    ///     When true, rebuild CodeFenceSymbols (and BM25 once that lands).
    /// </summary>
    public bool RebuildIndexes { get; init; } = true;

    /// <summary>
    ///     Optional cap for spot-checking a large library — process only
    ///     this many chunks.
    /// </summary>
    public int? MaxChunks { get; init; }
}
