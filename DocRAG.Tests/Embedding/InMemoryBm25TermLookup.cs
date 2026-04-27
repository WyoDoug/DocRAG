// // InMemoryBm25TermLookup.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using DocRAG.Core.Models;
using DocRAG.Ingestion.Embedding;

#endregion

namespace DocRAG.Tests.Embedding;

/// <summary>
///     Test-only IBm25TermLookup that flattens a builder's shards into an
///     in-memory dictionary. Lets the scorer tests exercise the production
///     async API without standing up MongoDB or the GridFS bucket.
/// </summary>
internal sealed class InMemoryBm25TermLookup : IBm25TermLookup
{
    public InMemoryBm25TermLookup(Bm25BuildResult build)
    {
        ArgumentNullException.ThrowIfNull(build);

        var allPostings = new Dictionary<string, IReadOnlyList<Bm25Posting>>(StringComparer.Ordinal);
        foreach(var shard in build.Shards)
            foreach(var (term, postings) in shard.InlineTerms)
                allPostings[term] = postings;
        mAllPostings = allPostings;
    }

    private readonly Dictionary<string, IReadOnlyList<Bm25Posting>> mAllPostings;

    public Task PreloadAsync(IReadOnlyList<string> queryTerms, CancellationToken ct = default) =>
        Task.CompletedTask;

    public IReadOnlyList<Bm25Posting> GetPostings(string term) =>
        mAllPostings.TryGetValue(term, out var found) ? found : [];
}
