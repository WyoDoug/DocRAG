// RejectionAccumulator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Symbols;

#endregion

namespace SaddleRAG.Ingestion.Recon;

/// <summary>
///     Aggregates per-chunk RejectedToken reports across a rescrub pass
///     into a list of ExcludedSymbol entries ready to upsert. For each
///     token name:
///       — Records the first reason seen (extractor reasons are
///         deterministic from the token shape, so conflicts are rare).
///       — Increments ChunkCount on every report.
///       — Captures up to three sample snippets, one per third of the
///         chunk stream (so spread-out noise produces spread-out samples).
///
///     Use by RescrubService: construct once with the total chunk count,
///     call Record per (chunk, rejectedToken), then Build at the end.
/// </summary>
public sealed class RejectionAccumulator
{
    public RejectionAccumulator(string libraryId, string version, int totalChunks)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        if (totalChunks < 1)
            throw new ArgumentOutOfRangeException(nameof(totalChunks), totalChunks, "totalChunks must be >= 1");

        mLibraryId = libraryId;
        mVersion = version;
        mTotalChunks = totalChunks;
    }

    private readonly string mLibraryId;
    private readonly string mVersion;
    private readonly int mTotalChunks;
    private readonly Dictionary<string, AccumulatorEntry> mEntries = new(StringComparer.Ordinal);

    /// <summary>
    ///     Record one rejection observation. <paramref name="chunkIndex"/>
    ///     is the position of the chunk in the rescrub iteration order;
    ///     used to bucket the sample by corpus third.
    /// </summary>
    public void Record(RejectedToken token, int chunkIndex, string chunkContent)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(chunkContent);

        var entry = mEntries.TryGetValue(token.Name, out var existing)
                        ? existing
                        : NewEntry(token);
        entry.ChunkCount++;
        TryCaptureSample(entry, chunkIndex, chunkContent, token.Name);
        mEntries[token.Name] = entry;
    }

    /// <summary>
    ///     Materialize the accumulator state into ExcludedSymbol records
    ///     ready for IExcludedSymbolsRepository.UpsertManyAsync.
    /// </summary>
    public IReadOnlyList<ExcludedSymbol> Build()
    {
        var nowUtc = DateTime.UtcNow;
        var result = mEntries.Values.Select(entry => new ExcludedSymbol
                                                         {
                                                             Id = ExcludedSymbol.MakeId(mLibraryId, mVersion, entry.Name),
                                                             LibraryId = mLibraryId,
                                                             Version = mVersion,
                                                             Name = entry.Name,
                                                             Reason = entry.Reason,
                                                             SampleSentences = entry.Samples
                                                                                    .OfType<string>()
                                                                                    .ToList(),
                                                             ChunkCount = entry.ChunkCount,
                                                             CapturedUtc = nowUtc
                                                         })
                              .ToList();
        return result;
    }

    private AccumulatorEntry NewEntry(RejectedToken token) => new()
                                                                  {
                                                                      Name = token.Name,
                                                                      Reason = token.Reason,
                                                                      Samples = new string?[ThirdsBuckets],
                                                                      ChunkCount = 0
                                                                  };

    private void TryCaptureSample(AccumulatorEntry entry, int chunkIndex, string chunkContent, string tokenName)
    {
        var bucket = ResolveBucket(chunkIndex);
        var alreadyHaveSample = entry.Samples[bucket] != null;
        if (!alreadyHaveSample)
        {
            var sample = SampleWindowExtractor.Extract(chunkContent, tokenName);
            if (sample != null)
                entry.Samples[bucket] = sample;
        }
    }

    private int ResolveBucket(int chunkIndex)
    {
        var third = mTotalChunks / ThirdsBuckets;
        var safeThird = Math.Max(third, 1);
        var bucket = Math.Min(chunkIndex / safeThird, ThirdsBuckets - 1);
        return bucket;
    }

    private sealed class AccumulatorEntry
    {
        public required string Name { get; init; }
        public required SymbolRejectionReason Reason { get; init; }
        public required string?[] Samples { get; init; }
        public int ChunkCount { get; set; }
    }

    private const int ThirdsBuckets = 3;
}
