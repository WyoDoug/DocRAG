// InMemoryBruteForceVectorSearch.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>
///     In-memory brute-force vector search for local development.
///     Per-profile isolation: each profile has its own set of indices.
///     Production should use MongoDB Atlas vector search.
/// </summary>
public class InMemoryBruteForceVectorSearch : IVectorSearchProvider
{
    /// <summary>
    ///     Index keyed by "{profile}/{libraryId}/{version}".
    ///     Default profile uses empty string.
    /// </summary>
    private readonly Dictionary<string, List<DocChunk>> mIndices = new Dictionary<string, List<DocChunk>>();

    private readonly object mLock = new object();

    /// <inheritdoc />
    public Task IndexChunksAsync(string? profile,
                                 string libraryId,
                                 string version,
                                 IReadOnlyList<DocChunk> chunks,
                                 CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentNullException.ThrowIfNull(chunks);

        string key = MakeKey(profile, libraryId, version);
        var embeddedChunks = chunks.Where(c => c.Embedding != null).ToList();

        lock(mLock)
        {
            mIndices[key] = embeddedChunks;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(float[] queryEmbedding,
                                                               VectorSearchFilter filter,
                                                               int maxResults = 5,
                                                               CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(queryEmbedding);
        ArgumentNullException.ThrowIfNull(filter);

        var candidates = new List<(DocChunk Chunk, float Score)>();
        string profilePrefix = MakeProfilePrefix(filter.Profile);

        lock(mLock)
        {
            var targetIndices = ResolveTargetIndices(filter, profilePrefix);

            foreach((string _, var chunks) in targetIndices)
                CollectScoredCandidates(chunks, filter, queryEmbedding, candidates);
        }

        var results = candidates
                      .OrderByDescending(x => x.Score)
                      .Take(maxResults)
                      .Select(x => new VectorSearchResult
                                       {
                                           Chunk = x.Chunk,
                                           Score = x.Score
                                       }
                             )
                      .ToList();

        return Task.FromResult<IReadOnlyList<VectorSearchResult>>(results);
    }

    private static void CollectScoredCandidates(List<DocChunk> chunks,
                                                VectorSearchFilter filter,
                                                float[] queryEmbedding,
                                                List<(DocChunk Chunk, float Score)> candidates)
    {
        var filtered = filter.Category.HasValue
                           ? chunks.Where(c => c.Category == filter.Category.Value)
                           : chunks;

        foreach(var chunk in filtered.Where(c => c.Embedding != null))
        {
            float[]? embedding = chunk.Embedding;
            if (embedding != null)
            {
                float score = CosineSimilarity(queryEmbedding, embedding);
                candidates.Add((chunk, score));
            }
        }
    }

    private IEnumerable<KeyValuePair<string, List<DocChunk>>> ResolveTargetIndices(
        VectorSearchFilter filter,
        string profilePrefix)
    {
        var result = (filter.LibraryId, filter.Version) switch
            {
                (not null, not null) => ResolveExactIndex(filter, profilePrefix),
                (not null, null) => mIndices.Where(kv => kv.Key.StartsWith($"{profilePrefix}{filter.LibraryId}/")),
                var _ => mIndices.Where(kv => kv.Key.StartsWith(profilePrefix))
            };
        return result;
    }

    private IEnumerable<KeyValuePair<string, List<DocChunk>>> ResolveExactIndex(
        VectorSearchFilter filter,
        string profilePrefix)
    {
        string libraryId = filter.LibraryId ?? string.Empty;
        string version = filter.Version ?? string.Empty;
        string key = MakeKey(filter.Profile, libraryId, version);
        IEnumerable<KeyValuePair<string, List<DocChunk>>> result = mIndices.ContainsKey(key)
                                                                       ?
                                                                           [
                                                                               new KeyValuePair<string,
                                                                                   List<DocChunk>>(key,
                                                                                            mIndices[key]
                                                                                       )
                                                                           ]
                                                                       : [];
        return result;
    }

    private static string MakeKey(string? profile, string libraryId, string version)
    {
        string profilePart = profile ?? string.Empty;
        return $"{profilePart}/{libraryId}/{version}";
    }

    private static string MakeProfilePrefix(string? profile)
    {
        string profilePart = profile ?? string.Empty;
        return $"{profilePart}/";
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        var result = 0f;

        if (a.Length == b.Length)
        {
            float dotProduct = 0;
            float normA = 0;
            float normB = 0;

            for(var i = 0; i < a.Length; i++)
            {
                dotProduct += a[i] * b[i];
                normA += a[i] * a[i];
                normB += b[i] * b[i];
            }

            float denominator = MathF.Sqrt(normA) * MathF.Sqrt(normB);
            result = denominator > 0 ? dotProduct / denominator : 0f;
        }

        return result;
    }
}
