// IBm25TermLookup.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>
///     Resolves BM25 postings for a term, hiding the sharded storage and
///     GridFS spill from the scorer. Implementations may pre-load shards,
///     cache, or fetch lazily — the scorer just asks for postings by
///     term.
/// </summary>
public interface IBm25TermLookup
{
    /// <summary>
    ///     Pre-fetch the storage backing whichever shards the supplied
    ///     query terms hash into. Lets the scorer remain synchronous —
    ///     all the I/O is settled before scoring starts. Idempotent;
    ///     calling again with the same terms is cheap (cache hit).
    /// </summary>
    Task PreloadAsync(IReadOnlyList<string> queryTerms, CancellationToken ct = default);

    /// <summary>
    ///     Get postings for a term. Returns an empty list if the term is
    ///     not in the index. Must NOT block on I/O — call
    ///     <see cref="PreloadAsync"/> first for any term you'll look up.
    /// </summary>
    IReadOnlyList<Bm25Posting> GetPostings(string term);
}
