// DocChunk.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Core.Enums;

#endregion

namespace SaddleRAG.Core.Models;

/// <summary>
///     A retrieval-sized chunk of documentation content with its embedding vector.
/// </summary>
public record DocChunk
{
    /// <summary>
    ///     Unique identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     Parent library identifier.
    /// </summary>
    public required string LibraryId { get; init; }

    /// <summary>
    ///     Version of the library this chunk belongs to.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    ///     URL of the source page.
    /// </summary>
    public required string PageUrl { get; init; }

    /// <summary>
    ///     Title of the source page.
    /// </summary>
    public required string PageTitle { get; init; }

    /// <summary>
    ///     Documentation category.
    /// </summary>
    public required DocCategory Category { get; init; }

    /// <summary>
    ///     The chunk text content.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    ///     Embedding vector. Null until embedded.
    /// </summary>
    public float[]? Embedding { get; init; }

    /// <summary>
    ///     Approximate token count of Content.
    /// </summary>
    public int TokenCount { get; init; }

    /// <summary>
    ///     Heading path within the page. Example: "XamDataGrid > Editing > Cell Editors"
    /// </summary>
    public string? SectionPath { get; init; }

    /// <summary>
    ///     For ApiReference chunks: the fully qualified class/member name.
    /// </summary>
    public string? QualifiedName { get; init; }

    /// <summary>
    ///     For Sample chunks: the language of the primary code block.
    /// </summary>
    public string? CodeLanguage { get; init; }

    /// <summary>
    ///     Tags extracted from content for faceted search.
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    ///     Identifier symbols extracted from Content by the symbol extractor.
    ///     Each symbol carries a SymbolKind (Type, Enum, Function, etc.) so
    ///     the per-kind list tools can filter without re-parsing. The chunk's
    ///     QualifiedName is the most prominent symbol's Name, kept for
    ///     back-compat with list_classes.
    /// </summary>
    public IReadOnlyList<Symbol> Symbols { get; init; } = [];

    /// <summary>
    ///     Version of the symbol-extractor logic that produced Symbols and
    ///     QualifiedName. Bumped whenever extractor keep/reject rules, token
    ///     shapes, or SymbolKind taxonomy change. start_ingest flips a library
    ///     to STALE when any chunk has ParserVersion below the codebase's
    ///     CurrentParserVersion. Defaults to 1 for legacy rows so existing
    ///     data reads cleanly without a destructive migration.
    /// </summary>
    public int ParserVersion { get; init; } = 1;
}
