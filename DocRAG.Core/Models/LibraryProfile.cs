// LibraryProfile.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace DocRAG.Core.Models;

/// <summary>
///     Pre-scrape reconnaissance result for a documentation library. Drives
///     the identifier-aware extractor, ranking heuristics, and per-library
///     token rules without hand-coded registries. Produced by the calling
///     LLM (preferred) or a configurable local Ollama fallback.
/// </summary>
public record LibraryProfile
{
    /// <summary>
    ///     Mongo document id. Format: "{LibraryId}/{Version}".
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     Schema version of this profile. Bumped when LibraryProfile shape
    ///     changes so we can migrate cached profiles forward instead of
    ///     invalidating them.
    /// </summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>
    ///     Library identifier the profile applies to.
    /// </summary>
    public required string LibraryId { get; init; }

    /// <summary>
    ///     Version of the library this profile applies to.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    ///     Primary documentation language(s) — for example "C#", "Python",
    ///     "AeroScript". Drives which declared-form patterns apply.
    /// </summary>
    public IReadOnlyList<string> Languages { get; init; } = [];

    /// <summary>
    ///     Per-category casing conventions used by this library's docs.
    /// </summary>
    public CasingConventions Casing { get; init; } = new();

    /// <summary>
    ///     Token-joining symbols valid for this library — for example
    ///     ".", "::", "->", ":".
    /// </summary>
    public IReadOnlyList<string> Separators { get; init; } = [];

    /// <summary>
    ///     Recognized callable shapes — for example "Foo()", "Foo&lt;T&gt;()".
    /// </summary>
    public IReadOnlyList<string> CallableShapes { get; init; } = [];

    /// <summary>
    ///     Boost set: identifiers recon believes are real types/functions in
    ///     this library. NOT an allowlist — symbols missing from this list
    ///     can still survive extraction via corpus-context rules.
    /// </summary>
    public IReadOnlyList<string> LikelySymbols { get; init; } = [];

    /// <summary>
    ///     URL of a canonical inventory page (for example an enum index
    ///     page) when recon spots one. Optional.
    /// </summary>
    public string? CanonicalInventoryUrl { get; init; }

    /// <summary>
    ///     Recon's self-reported confidence in the profile, 0..1. Used by the
    ///     CLI fallback to refuse persisting low-confidence profiles.
    /// </summary>
    public float Confidence { get; init; }

    /// <summary>
    ///     Where the profile came from: "calling-llm", "cli-ollama", "manual".
    /// </summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    ///     UTC time the profile was created.
    /// </summary>
    public DateTime CreatedUtc { get; init; }

    /// <summary>
    ///     Current schema version emitted by this codebase.
    /// </summary>
    public const int CurrentSchemaVersion = 1;
}
