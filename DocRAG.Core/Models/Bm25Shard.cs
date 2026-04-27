// // Bm25Shard.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

namespace DocRAG.Core.Models;

/// <summary>
///     One partition of a (library, version)'s BM25 inverted index. Terms
///     are partitioned across shards by <see cref="ShardIndex"/> using a
///     stable hash function; the writer decides at persist-time whether to
///     keep each term's postings inline in <see cref="InlineTerms"/> or
///     spill to GridFS via <see cref="ExternalTerms"/>. If the shard
///     itself would still exceed the 16MB Mongo document limit after
///     per-term spill, the entire shard is uploaded to GridFS and only
///     <see cref="ShardGridFsRef"/> is populated.
/// </summary>
public record Bm25Shard
{
    /// <summary>
    ///     Mongo document id. Format: "{LibraryId}/{Version}/{ShardIndex}".
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     Library this shard belongs to.
    /// </summary>
    public required string LibraryId { get; init; }

    /// <summary>
    ///     Version this shard belongs to.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    ///     Zero-based shard index. Terms hash to <c>hash(term) mod ShardCount</c>.
    /// </summary>
    public required int ShardIndex { get; init; }

    /// <summary>
    ///     Term → list of postings, stored inline. Always populated for
    ///     terms whose postings list serializes under the per-term
    ///     threshold. Empty when the entire shard spilled to GridFS.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Bm25Posting>> InlineTerms { get; init; }
        = new Dictionary<string, IReadOnlyList<Bm25Posting>>();

    /// <summary>
    ///     Term → GridFS file id (as string) for terms whose postings
    ///     exceed the per-term inline threshold. Reader fetches the
    ///     postings from GridFS on demand.
    /// </summary>
    public IReadOnlyDictionary<string, string> ExternalTerms { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    ///     GridFS file id (as string) holding the entire shard payload
    ///     when the shard could not be persisted inline even after
    ///     per-term spill. Null in the common case. When set,
    ///     <see cref="InlineTerms"/> and <see cref="ExternalTerms"/> are
    ///     empty and the reader fetches everything from GridFS.
    /// </summary>
    public string? ShardGridFsRef { get; init; }
}
