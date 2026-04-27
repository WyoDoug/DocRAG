// Bm25IndexBuilder.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.Text.RegularExpressions;
using DocRAG.Core.Models;

#endregion

namespace DocRAG.Ingestion.Embedding;

/// <summary>
///     Builds a sharded BM25 inverted index from a library's chunk
///     content. Postings are partitioned across <see cref="DefaultShardCount"/>
///     buckets via stable hash on the term, so any single shard fits
///     comfortably in a Mongo document for the corpus sizes DocRAG is
///     built for. The repository layer applies further per-term and
///     per-shard GridFS spill at write time as a robustness net.
///
///     Tokenization: lowercased word characters, with original-cased
///     identifier-shaped tokens (PascalCase, dotted, ::-joined) ALSO
///     emitted as-is so identifier queries match. This dual emission is
///     what makes BM25 useful for identifier-heavy doc corpora.
/// </summary>
public static class Bm25IndexBuilder
{
    /// <summary>
    ///     Build a sharded BM25 index over the supplied chunks. Reads
    ///     each chunk's Id and Content fields; produces inverted postings
    ///     keyed by term, partitioned into <paramref name="shardCount"/>
    ///     shards. Returns the inline <see cref="Bm25Stats"/> and the
    ///     full shard list — caller persists shards via
    ///     <c>IBm25ShardRepository.ReplaceShardsAsync</c>.
    /// </summary>
    public static Bm25BuildResult Build(string libraryId,
                                        string version,
                                        IReadOnlyList<DocChunk> chunks,
                                        int shardCount = DefaultShardCount)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentNullException.ThrowIfNull(chunks);
        if (shardCount < 1)
            throw new ArgumentOutOfRangeException(nameof(shardCount), shardCount, "shardCount must be >= 1");

        var postings = new Dictionary<string, List<Bm25Posting>>(StringComparer.Ordinal);
        var docLengths = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach(var chunk in chunks.Where(c => !string.IsNullOrEmpty(c.Content)))
            IndexOne(chunk, postings, docLengths);

        var documentCount = docLengths.Count;
        var avg = documentCount > 0 ? docLengths.Values.Average() : 0.0;

        var shards = PartitionIntoShards(libraryId, version, postings, shardCount);
        var stats = new Bm25Stats
                        {
                            DocLengths = docLengths,
                            DocumentCount = documentCount,
                            AverageDocLength = avg,
                            ShardCount = shardCount
                        };

        var result = new Bm25BuildResult { Stats = stats, Shards = shards };
        return result;
    }

    /// <summary>
    ///     Stable, non-cryptographic hash for term → shard assignment.
    ///     Public so the reader (term lookup) uses the SAME hash to
    ///     route a query term to its shard — divergence here means
    ///     missed lookups.
    /// </summary>
    public static int ShardIndexFor(string term, int shardCount)
    {
        ArgumentException.ThrowIfNullOrEmpty(term);
        if (shardCount < 1)
            throw new ArgumentOutOfRangeException(nameof(shardCount), shardCount, "shardCount must be >= 1");

        unchecked
        {
            uint hash = HashSeed;
            foreach(var c in term)
                hash = (hash * HashMultiplier) ^ c;
            var result = (int) (hash % (uint) shardCount);
            return result;
        }
    }

    private static IReadOnlyList<Bm25Shard> PartitionIntoShards(string libraryId,
                                                                string version,
                                                                Dictionary<string, List<Bm25Posting>> postings,
                                                                int shardCount)
    {
        var buckets = new Dictionary<string, IReadOnlyList<Bm25Posting>>[shardCount];
        for(var i = 0; i < shardCount; i++)
            buckets[i] = new Dictionary<string, IReadOnlyList<Bm25Posting>>(StringComparer.Ordinal);

        foreach(var (term, list) in postings)
        {
            var idx = ShardIndexFor(term, shardCount);
            ((Dictionary<string, IReadOnlyList<Bm25Posting>>) buckets[idx])[term] = list;
        }

        var shards = new List<Bm25Shard>(shardCount);
        for(var i = 0; i < shardCount; i++)
        {
            var inline = buckets[i];
            if (inline.Count > 0)
                shards.Add(new Bm25Shard
                               {
                                   Id = $"{libraryId}/{version}/{i}",
                                   LibraryId = libraryId,
                                   Version = version,
                                   ShardIndex = i,
                                   InlineTerms = inline
                               });
        }

        return shards;
    }

    private static void IndexOne(DocChunk chunk,
                                 Dictionary<string, List<Bm25Posting>> postings,
                                 Dictionary<string, int> docLengths)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        // 1) Lowercase prose tokens.
        foreach(Match match in smProseTokenRegex.Matches(chunk.Content).Where(m => m.Value.Length >= MinTokenLength))
        {
            var lower = match.Value.ToLowerInvariant();
            counts[lower] = counts.TryGetValue(lower, out var c) ? c + 1 : 1;
        }

        // 2) Original-cased identifier tokens (preserve case so
        //    PascalCase queries match without lowercasing).
        foreach(Match match in smIdentifierTokenRegex.Matches(chunk.Content).Where(m => m.Value.Length >= MinTokenLength))
        {
            var raw = match.Value;
            counts[raw] = counts.TryGetValue(raw, out var c) ? c + 1 : 1;
        }

        var docLength = counts.Values.Sum();
        if (docLength > 0)
        {
            docLengths[chunk.Id] = docLength;
            foreach(var (term, freq) in counts)
            {
                var posting = new Bm25Posting { ChunkId = chunk.Id, TermFrequency = freq };
                if (!postings.TryGetValue(term, out var list))
                {
                    list = [];
                    postings[term] = list;
                }
                list.Add(posting);
            }
        }
    }

    // Prose tokens: word characters, lowercased after match.
    private static readonly Regex smProseTokenRegex = new(
        @"[A-Za-z][A-Za-z0-9]+",
        RegexOptions.Compiled
    );

    // Identifier-shaped tokens: PascalCase, dotted, ::-joined, snake_case.
    private static readonly Regex smIdentifierTokenRegex = new(
        @"[A-Za-z_][A-Za-z0-9_]*(?:(?:\.|::|->)[A-Za-z_][A-Za-z0-9_]*)*",
        RegexOptions.Compiled
    );

    private const int MinTokenLength = 2;
    private const int DefaultShardCount = 64;

    // FNV-1a-style mixing constants. Stable across runs / processes.
    private const uint HashSeed = 2166136261u;
    private const uint HashMultiplier = 16777619u;
}
