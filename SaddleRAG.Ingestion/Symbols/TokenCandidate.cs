// TokenCandidate.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace SaddleRAG.Ingestion.Symbols;

/// <summary>
///     A candidate identifier token produced by the tokenizer. Carries the
///     trimmed name plus structural hints the SymbolExtractor uses to
///     decide kind and apply keep rules.
/// </summary>
public record TokenCandidate
{
    /// <summary>
    ///     Trimmed identifier (no surrounding punctuation). For dotted
    ///     candidates this is the entire dotted path ("Foo.Bar.Baz").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     For dotted/::-joined candidates, the rightmost segment.
    ///     For "Foo.Bar.Baz", LeafName is "Baz". Equal to Name for
    ///     undotted identifiers.
    /// </summary>
    public required string LeafName { get; init; }

    /// <summary>
    ///     For dotted/::-joined candidates, everything to the left of the
    ///     final separator. For "Foo.Bar.Baz", Container is "Foo.Bar".
    ///     Null for undotted identifiers.
    /// </summary>
    public string? Container { get; init; }

    /// <summary>
    ///     True when the token is immediately followed by "(" — a callable
    ///     shape signal.
    /// </summary>
    public bool HasCallableShape { get; init; }

    /// <summary>
    ///     True when the token is immediately followed by "&lt;" — a
    ///     generic-type signal.
    /// </summary>
    public bool HasGenericShape { get; init; }

    /// <summary>
    ///     True when the token is preceded by a declared-form keyword
    ///     ("class", "interface", "struct", "enum", "record", "type",
    ///     "def", "function"). When set, the candidate is almost certainly
    ///     a real symbol regardless of other signals.
    /// </summary>
    public bool IsDeclared { get; init; }

    /// <summary>
    ///     The declared-form keyword when IsDeclared is true. Used to
    ///     classify the SymbolKind ("class" → Type, "enum" → Enum, etc.).
    /// </summary>
    public string? DeclaredFormKeyword { get; init; }
}
