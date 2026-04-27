// RescrubResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace DocRAG.Core.Models;

/// <summary>
///     Summary returned by rescrub_library. Counts what was processed,
///     what changed, whether classification re-ran, whether indexes were
///     rebuilt, and a sample of per-chunk diffs (full list for dry-run,
///     first ten otherwise).
/// </summary>
public record RescrubResult
{
    /// <summary>
    ///     Library the rescrub applied to.
    /// </summary>
    public required string LibraryId { get; init; }

    /// <summary>
    ///     Version the rescrub applied to.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    ///     Number of chunks examined.
    /// </summary>
    public int Processed { get; init; }

    /// <summary>
    ///     Number of chunks whose Symbols, QualifiedName, ParserVersion,
    ///     or Category changed.
    /// </summary>
    public int Changed { get; init; }

    /// <summary>
    ///     Number of chunks whose content matches the chunk-boundary audit
    ///     heuristics (trailing-period last token, leading-period first
    ///     token). High count here suggests a re-chunk pass is needed
    ///     (separate tool, separate branch).
    /// </summary>
    public int BoundaryIssues { get; init; }

    /// <summary>
    ///     True when rescrub also re-ran the LLM classifier. Decided by
    ///     RescrubOptions.ReClassify when explicit, or auto-detected from
    ///     LibraryManifest history.
    /// </summary>
    public bool DidReclassify { get; init; }

    /// <summary>
    ///     True when CodeFenceSymbols (and future BM25 index) were
    ///     rebuilt as part of this rescrub call.
    /// </summary>
    public bool IndexesBuilt { get; init; }

    /// <summary>
    ///     True when this run made no writes (dry-run mode).
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    ///     Sample of per-chunk diffs. Full list when DryRun is true,
    ///     first ten otherwise.
    /// </summary>
    public IReadOnlyList<RescrubDiff> SampleDiffs { get; init; } = [];

    /// <summary>
    ///     Returned when the (library, version) has no LibraryProfile yet;
    ///     caller should run recon_library and submit_library_profile first.
    /// </summary>
    public bool ReconNeeded { get; init; }

    /// <summary>
    ///     Total number of distinct tokens the extractor rejected during
    ///     this rescrub. Zero when the rescrub was a no-op (ReconNeeded or
    ///     no chunks).
    /// </summary>
    public int ExcludedCount { get; init; }

    /// <summary>
    ///     Optional advisory text the calling LLM may surface to the user.
    ///     Populated only when the rescrub crossed both threshold values
    ///     (≥5% of candidates excluded AND ≥20 absolute exclusions);
    ///     otherwise empty.
    /// </summary>
    public IReadOnlyList<string> Hints { get; init; } = [];
}
