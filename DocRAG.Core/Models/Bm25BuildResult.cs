// // Bm25BuildResult.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

namespace DocRAG.Core.Models;

/// <summary>
///     Output of <c>Bm25IndexBuilder.Build</c> — the inline stats that
///     ride along in the LibraryIndex document plus the shard list that
///     gets persisted via the BM25 shard repository.
/// </summary>
public record Bm25BuildResult
{
    /// <summary>
    ///     Inline metadata: DocLengths, DocumentCount, AverageDocLength,
    ///     ShardCount.
    /// </summary>
    public required Bm25Stats Stats { get; init; }

    /// <summary>
    ///     One <see cref="Bm25Shard"/> per non-empty bucket. Shards arrive
    ///     fully inline; the repository decides spill-to-GridFS at write
    ///     time based on serialized size.
    /// </summary>
    public required IReadOnlyList<Bm25Shard> Shards { get; init; }
}
