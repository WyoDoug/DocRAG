// // Bm25IndexBuilder.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using System.Text.RegularExpressions;
using DocRAG.Core.Models;

#endregion

namespace DocRAG.Ingestion.Embedding;

/// <summary>
///     Builds a Bm25Index from a library's chunk content. Used at
///     ingestion time and when rescrub_library bootstraps or rebuilds
///     library_indexes for a (library, version).
///
///     Tokenization: lowercased word characters, with original-cased
///     identifier-shaped tokens (PascalCase, dotted, ::-joined) ALSO
///     emitted as-is so identifier queries match. This dual emission is
///     what makes BM25 useful for identifier-heavy doc corpora.
/// </summary>
public static class Bm25IndexBuilder
{
    /// <summary>
    ///     Build a Bm25Index over the supplied chunks. Reads each chunk's
    ///     Id and Content fields; produces inverted postings keyed by
    ///     term (case-sensitive for identifier tokens, lowercased for
    ///     prose words).
    /// </summary>
    public static Bm25Index Build(IReadOnlyList<DocChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        var postings = new Dictionary<string, List<Bm25Posting>>(StringComparer.Ordinal);
        var docLengths = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach(var chunk in chunks.Where(c => !string.IsNullOrEmpty(c.Content)))
            IndexOne(chunk, postings, docLengths);

        var documentCount = docLengths.Count;
        var avg = documentCount > 0 ? docLengths.Values.Average() : 0.0;

        var frozen = postings.ToDictionary(kv => kv.Key,
                                           kv => (IReadOnlyList<Bm25Posting>) kv.Value,
                                           StringComparer.Ordinal
                                          );

        var result = new Bm25Index
                         {
                             Postings = frozen,
                             DocLengths = docLengths,
                             DocumentCount = documentCount,
                             AverageDocLength = avg
                         };
        return result;
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
}
