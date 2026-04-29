// Bm25Scorer.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.Text.RegularExpressions;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>
///     Standard BM25 scoring against a sharded postings store. The scorer
///     extracts query terms, asks the supplied <see cref="IBm25TermLookup"/>
///     to pre-load whichever shards back those terms, then computes scores
///     synchronously against the cached postings. Used by the hybrid
///     retrieval path to blend keyword scores with vector cosine
///     similarity, especially valuable for identifier queries where
///     embedding similarity alone struggles to discriminate between
///     "mentions term" and "is canonical reference for term".
///
///     k1 = 1.5, b = 0.75 are the standard defaults; configurable for
///     bench tuning later if needed.
/// </summary>
public static class Bm25Scorer
{
    /// <summary>
    ///     Score every chunk against the query. Returns a dictionary
    ///     keyed by chunk id; chunks with score 0 are omitted. Scores
    ///     are not normalized — caller can normalize to [0,1] for
    ///     blending with vector cosine.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, double>> ScoreAsync(IBm25TermLookup termLookup,
                                                                             Bm25Stats stats,
                                                                             string query,
                                                                             CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(termLookup);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentException.ThrowIfNullOrEmpty(query);

        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        if (stats.DocumentCount > 0)
        {
            var queryTerms = ExtractQueryTerms(query);
            await termLookup.PreloadAsync(queryTerms, ct);
            ScoreNonEmptyIndex(termLookup, stats, queryTerms, scores);
        }

        return scores;
    }

    /// <summary>
    ///     Score and return the top-N chunk ids ordered by score desc.
    ///     Convenience wrapper around <see cref="ScoreAsync"/>.
    /// </summary>
    public static async Task<IReadOnlyList<(string ChunkId, double Score)>> TopNAsync(IBm25TermLookup termLookup,
                                                                                      Bm25Stats stats,
                                                                                      string query,
                                                                                      int n,
                                                                                      CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(termLookup);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentException.ThrowIfNullOrEmpty(query);

        if (n <= 0)
            throw new ArgumentOutOfRangeException(nameof(n), n, "n must be positive");

        var scores = await ScoreAsync(termLookup, stats, query, ct);
        var ordered = scores.OrderByDescending(kv => kv.Value)
                            .Take(n)
                            .Select(kv => (kv.Key, kv.Value))
                            .ToList();
        return ordered;
    }

    private static void ScoreNonEmptyIndex(IBm25TermLookup termLookup,
                                           Bm25Stats stats,
                                           IReadOnlyList<string> queryTerms,
                                           Dictionary<string, double> scores)
    {
        var avgLength = stats.AverageDocLength > 0 ? stats.AverageDocLength : 1.0;

        foreach(var term in queryTerms)
        {
            var postings = termLookup.GetPostings(term);
            if (postings.Count > 0)
            {
                var idf = ComputeIdf(stats.DocumentCount, postings.Count);
                foreach(var posting in postings)
                    AddTermScore(stats, scores, posting, idf, avgLength);
            }
        }
    }

    private static void AddTermScore(Bm25Stats stats,
                                     Dictionary<string, double> scores,
                                     Bm25Posting posting,
                                     double idf,
                                     double avgLength)
    {
        var docLength = stats.DocLengths.TryGetValue(posting.ChunkId, out var len) ? len : (int) avgLength;
        var termFreq = posting.TermFrequency;
        var lengthNorm = 1.0 - B + (B * (docLength / avgLength));
        var numerator = termFreq * (K1 + 1.0);
        var denominator = termFreq + (K1 * lengthNorm);
        var contribution = idf * (numerator / denominator);
        scores[posting.ChunkId] = scores.TryGetValue(posting.ChunkId, out var current) ? current + contribution : contribution;
    }

    private static double ComputeIdf(int documentCount, int postingCount)
    {
        var numerator = documentCount - postingCount + 0.5;
        var denominator = postingCount + 0.5;
        var ratio = (numerator / denominator) + 1.0;
        var result = Math.Log(ratio);
        return result;
    }

    /// <summary>
    ///     Tokenize a query the SAME way <see cref="Bm25IndexBuilder"/>
    ///     tokenizes chunks. Lowercased prose tokens AND original-cased
    ///     identifier tokens, deduped. Public so callers (e.g.,
    ///     <c>SearchTools</c>) can pre-feed the term list into the
    ///     lookup if they want.
    /// </summary>
    public static IReadOnlyList<string> ExtractQueryTerms(string query)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);

        var terms = new List<string>();

        // Lowercase prose tokens.
        foreach(Match match in smProseTokenRegex.Matches(query).Where(m => m.Value.Length >= MinTokenLength))
            terms.Add(match.Value.ToLowerInvariant());

        // Original-cased identifier tokens.
        foreach(Match match in smIdentifierTokenRegex.Matches(query).Where(m => m.Value.Length >= MinTokenLength))
            terms.Add(match.Value);

        var distinct = terms.Distinct(StringComparer.Ordinal).ToList();
        return distinct;
    }

    private static readonly Regex smProseTokenRegex = new(
        @"[A-Za-z][A-Za-z0-9]+",
        RegexOptions.Compiled
    );

    private static readonly Regex smIdentifierTokenRegex = new(
        @"[A-Za-z_][A-Za-z0-9_]*(?:(?:\.|::|->)[A-Za-z_][A-Za-z0-9_]*)*",
        RegexOptions.Compiled
    );

    private const int MinTokenLength = 2;
    private const double K1 = 1.5;
    private const double B = 0.75;
}
