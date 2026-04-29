// SymbolRejectionReason.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace SaddleRAG.Core.Enums;

/// <summary>
///     Why an identifier-shaped token did NOT survive the symbol extractor.
///     Surfaced via the new library_excluded_symbols collection so a calling
///     LLM can triage what was filtered.
/// </summary>
public enum SymbolRejectionReason
{
    /// <summary>
    ///     Hit the universal Stoplist (English stopwords, doc-callout words,
    ///     UI button labels, programming-prose nouns).
    /// </summary>
    GlobalStoplist,

    /// <summary>
    ///     Hit the per-library deny list on LibraryProfile.Stoplist.
    /// </summary>
    LibraryStoplist,

    /// <summary>
    ///     Matched UnitsLookup (mm, GHz, RPM, etc.).
    /// </summary>
    Unit,

    /// <summary>
    ///     Token shorter than the 2-character minimum.
    /// </summary>
    BelowMinLength,

    /// <summary>
    ///     The prose-frequent keep rule was the only path that could have
    ///     saved this token, but the IsLikelyAbbreviation guard blocked it
    ///     (short all-uppercase, looks like an acronym).
    /// </summary>
    LikelyAbbreviation,

    /// <summary>
    ///     Token failed every keep rule in ShouldKeep — no declared form,
    ///     not in LikelySymbols, no code-fence appearance, no container,
    ///     no internal structure, no callable/generic shape, not prose-
    ///     frequent. The catch-all reason for tokens that just don't look
    ///     like symbols.
    /// </summary>
    NoStructureSignal
}
