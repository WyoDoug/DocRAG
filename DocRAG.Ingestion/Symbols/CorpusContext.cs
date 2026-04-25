// // CorpusContext.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

namespace DocRAG.Ingestion.Symbols;

/// <summary>
///     Cross-chunk context required for the keep-rules that look outside
///     a single chunk: "appears in code fence anywhere in the corpus" and
///     "appears N+ times capitalized in prose across the library". On the
///     first ingest pass, the corpus index does not yet exist so these
///     rules are skipped; rescrub_library populates the index and re-runs
///     extraction with a populated context.
/// </summary>
public record CorpusContext
{
    /// <summary>
    ///     Set of identifier-shaped tokens that appear inside any code
    ///     fence anywhere in the (library, version)'s chunks.
    /// </summary>
    public IReadOnlySet<string> CodeFenceSymbols { get; init; } = new HashSet<string>();

    /// <summary>
    ///     Identifier → number of times it appears capitalized in prose
    ///     (outside code fences) across the library. Drives the
    ///     prose-mention backstop keep rule.
    /// </summary>
    public IReadOnlyDictionary<string, int> ProseMentionCounts { get; init; }
        = new Dictionary<string, int>();

    /// <summary>
    ///     Empty context used during the initial ingest pass before the
    ///     library_indexes are built.
    /// </summary>
    public static CorpusContext Empty { get; } = new();
}
