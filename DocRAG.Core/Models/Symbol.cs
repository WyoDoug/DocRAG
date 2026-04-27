// Symbol.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;

#endregion

namespace DocRAG.Core.Models;

/// <summary>
///     An identifier symbol extracted from chunk content. Multiple symbols
///     may be emitted per chunk; the chunk's QualifiedName field is set to
///     the most prominent one for back-compat with list_classes.
/// </summary>
public record Symbol
{
    /// <summary>
    ///     The bare identifier as it appears, with surrounding punctuation stripped.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     What kind of symbol this is.
    /// </summary>
    public required SymbolKind Kind { get; init; }

    /// <summary>
    ///     The containing identifier when the symbol was found in X.Member or
    ///     X::Member form. Null otherwise.
    /// </summary>
    public string? Container { get; init; }
}
