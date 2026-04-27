// IBm25ShardRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Models;

#endregion

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace DocRAG.Core.Interfaces;

/// <summary>
///     Storage for sharded BM25 inverted-index postings. Decouples the
///     scoring layer from MongoDB document-size constraints by writing
///     each (libraryId, version) as N shards, with per-term and per-shard
///     spill to GridFS for any payload that exceeds the inline limit.
/// </summary>
public interface IBm25ShardRepository
{
    /// <summary>
    ///     Replace all shards for (libraryId, version). Existing shards
    ///     are deleted first; supplied shards are written, with per-shard
    ///     and per-term GridFS spill applied automatically based on
    ///     serialized size. Resilient to interruption — partial writes
    ///     can be re-applied without dedup work since the delete is
    ///     scoped to (libraryId, version).
    /// </summary>
    Task ReplaceShardsAsync(string libraryId,
                            string version,
                            IReadOnlyList<Bm25Shard> shards,
                            CancellationToken ct = default);

    /// <summary>
    ///     Load a specific shard. Returns null if the shard does not
    ///     exist (no terms hashed to that shard index, or the library has
    ///     never been indexed). The returned shard's
    ///     <c>InlineTerms</c>/<c>ExternalTerms</c> are populated as-is —
    ///     external term postings are NOT auto-fetched; callers go
    ///     through <see cref="LoadPostingsAsync"/> to resolve them.
    /// </summary>
    Task<Bm25Shard?> GetShardAsync(string libraryId,
                                   string version,
                                   int shardIndex,
                                   CancellationToken ct = default);

    /// <summary>
    ///     Load all shards for (libraryId, version). Used by the rescrub
    ///     path and full-corpus operations; for query-time scoring,
    ///     prefer <see cref="GetShardAsync"/> per-term hashing to avoid
    ///     loading the full index.
    /// </summary>
    Task<IReadOnlyList<Bm25Shard>> GetAllShardsAsync(string libraryId,
                                                     string version,
                                                     CancellationToken ct = default);

    /// <summary>
    ///     Resolve a term's postings, fetching from GridFS if the term
    ///     was spilled, or loading the entire shard from GridFS if the
    ///     shard itself was spilled. Returns an empty list when the term
    ///     is not present in the shard (no chunks contain it).
    /// </summary>
    Task<IReadOnlyList<Bm25Posting>> LoadPostingsAsync(Bm25Shard shard,
                                                       string term,
                                                       CancellationToken ct = default);

    /// <summary>
    ///     Delete all shards and any GridFS payloads for (libraryId,
    ///     version). Used when a library version is removed entirely.
    /// </summary>
    Task DeleteShardsAsync(string libraryId,
                           string version,
                           CancellationToken ct = default);
}
