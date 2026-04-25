// // Bm25Scorer.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using System.Text.RegularExpressions;
using DocRAG.Core.Models;

#endregion

namespace DocRAG.Ingestion.Embedding;

/// <summary>
///     Standard BM25 scoring against a pre-built Bm25Index. Used by the
///     hybrid retrieval path to blend keyword scores with vector cosine
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
    ///     Score every chunk in the index against the query. Returns a
    ///     dictionary keyed by chunk id; chunks with score 0 are omitted.
    ///     Scores are not normalized — caller can normalize to [0,1] for
    ///     blending with vector cosine.
    /// </summary>
    public static IReadOnlyDictionary<string, double> Score(Bm25Index index, string query)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrEmpty(query);

        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        if (index.DocumentCount > 0)
            ScoreNonEmptyIndex(index, query, scores);

        return scores;
    }

    private static void ScoreNonEmptyIndex(Bm25Index index, string query, Dictionary<string, double> scores)
    {
        var queryTerms = ExtractQueryTerms(query);
        var avgLength = index.AverageDocLength > 0 ? index.AverageDocLength : 1.0;

        foreach(var (term, postings) in EnumerateMatchingPostings(index, queryTerms))
        {
            var idf = ComputeIdf(index.DocumentCount, postings.Count);
            foreach(var posting in postings)
                AddTermScore(index, scores, posting, idf, avgLength);
        }
    }

    private static IEnumerable<(string Term, IReadOnlyList<Bm25Posting> Postings)> EnumerateMatchingPostings(
        Bm25Index index,
        IReadOnlyList<string> queryTerms)
    {
        foreach(var term in queryTerms)
        {
            if (index.Postings.TryGetValue(term, out var postings))
                yield return (term, postings);
        }
    }

    /// <summary>
    ///     Score and return the top-N chunk ids ordered by score desc.
    ///     Convenience wrapper around <see cref="Score" />.
    /// </summary>
    public static IReadOnlyList<(string ChunkId, double Score)> TopN(Bm25Index index, string query, int n)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrEmpty(query);

        if (n <= 0)
            throw new ArgumentOutOfRangeException(nameof(n), n, "n must be positive");

        var scores = Score(index, query);
        var ordered = scores.OrderByDescending(kv => kv.Value)
                            .Take(n)
                            .Select(kv => (kv.Key, kv.Value))
                            .ToList();
        return ordered;
    }

    private static void AddTermScore(Bm25Index index,
                                     Dictionary<string, double> scores,
                                     Bm25Posting posting,
                                     double idf,
                                     double avgLength)
    {
        var docLength = index.DocLengths.TryGetValue(posting.ChunkId, out var len) ? len : (int) avgLength;
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

    private static IReadOnlyList<string> ExtractQueryTerms(string query)
    {
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
