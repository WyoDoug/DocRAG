// // ExtractedSymbols.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using DocRAG.Core.Models;

#endregion

namespace DocRAG.Ingestion.Symbols;

/// <summary>
///     Output of the symbol extractor for a single chunk: the full set of
///     surviving symbols plus the most-prominent one's name, used as the
///     chunk's QualifiedName for back-compat with list_classes.
/// </summary>
public record ExtractedSymbols
{
    /// <summary>
    ///     All symbols extracted from the chunk content.
    /// </summary>
    public required IReadOnlyList<Symbol> Symbols { get; init; }

    /// <summary>
    ///     The most prominent symbol's Name. Null when no symbols survived
    ///     the keep rules.
    /// </summary>
    public string? PrimaryQualifiedName { get; init; }

    /// <summary>
    ///     Empty result. Used when content has no surviving candidates.
    /// </summary>
    public static ExtractedSymbols Empty { get; } = new()
                                                        {
                                                            Symbols = Array.Empty<Symbol>()
                                                        };
}
