// // Bm25Index.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

namespace DocRAG.Core.Models;

/// <summary>
///     Inverted index supporting BM25 keyword scoring for hybrid retrieval.
///     Built per (library, version) at ingestion time and persisted in
///     LibraryIndex. The full scoring implementation lives in the Ingestion
///     project; this record is the storage shape only.
/// </summary>
public record Bm25Index
{
    /// <summary>
    ///     term → list of postings. Each posting names a chunk and how
    ///     many times the term appears in it.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Bm25Posting>> Postings { get; init; }
        = new Dictionary<string, IReadOnlyList<Bm25Posting>>();

    /// <summary>
    ///     Chunk id → token count for that chunk's content.
    /// </summary>
    public IReadOnlyDictionary<string, int> DocLengths { get; init; }
        = new Dictionary<string, int>();

    /// <summary>
    ///     Total number of chunks indexed.
    /// </summary>
    public int DocumentCount { get; init; }

    /// <summary>
    ///     Mean of DocLengths values. Cached so BM25 scoring does not have
    ///     to recompute it per query.
    /// </summary>
    public double AverageDocLength { get; init; }
}
