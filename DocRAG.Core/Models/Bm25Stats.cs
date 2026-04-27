// // Bm25Stats.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

namespace DocRAG.Core.Models;

/// <summary>
///     Inline-stored BM25 metadata that fits comfortably in the
///     LibraryIndex document. The actual postings live in the sharded
///     bm25Shards collection (with per-shard and per-term GridFS spill
///     for any shard or term too large to inline).
///
///     Stats are small even for very large libraries — DocLengths grows
///     with the number of chunks, which stays well under the 16MB Mongo
///     document limit for any realistic corpus.
/// </summary>
public record Bm25Stats
{
    /// <summary>
    ///     Chunk id → token count for that chunk's content. Used by the
    ///     scorer for length normalization; consulted per matching posting
    ///     so it must be available alongside the postings at score time.
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

    /// <summary>
    ///     Number of shards the postings were partitioned across. The
    ///     reader uses this to know how many lookup keys it might need to
    ///     visit, and to validate that the shard collection has the
    ///     expected number of documents for this (library, version).
    /// </summary>
    public int ShardCount { get; init; }
}
