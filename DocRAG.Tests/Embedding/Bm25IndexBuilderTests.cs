// Bm25IndexBuilderTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;
using DocRAG.Core.Models;
using DocRAG.Ingestion.Embedding;

#endregion

namespace DocRAG.Tests.Embedding;

public sealed class Bm25IndexBuilderTests
{
    [Fact]
    public void EmptyChunksProducesEmptyStatsAndNoShards()
    {
        var build = Bm25IndexBuilder.Build("lib", "1.0", []);

        Assert.Equal(0, build.Stats.DocumentCount);
        Assert.Equal(0.0, build.Stats.AverageDocLength);
        Assert.Empty(build.Shards);
    }

    [Fact]
    public void EveryTermLandsInExactlyOneShard()
    {
        var chunks = ManyChunks();
        var build = Bm25IndexBuilder.Build("lib", "1.0", chunks, shardCount: 16);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach(var shard in build.Shards)
            foreach(var term in shard.InlineTerms.Keys)
                counts[term] = counts.TryGetValue(term, out var c) ? c + 1 : 1;

        Assert.NotEmpty(counts);
        Assert.All(counts.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public void TermsHashIntoTheirAdvertisedShardIndex()
    {
        var chunks = ManyChunks();
        const int ShardCount = 16;

        var build = Bm25IndexBuilder.Build("lib", "1.0", chunks, shardCount: ShardCount);

        foreach(var shard in build.Shards)
            foreach(var term in shard.InlineTerms.Keys)
                Assert.Equal(shard.ShardIndex, Bm25IndexBuilder.ShardIndexFor(term, ShardCount));
    }

    [Fact]
    public void ShardIndexForIsDeterministicAcrossCalls()
    {
        var first = Bm25IndexBuilder.ShardIndexFor("MoveLinear", 64);
        var second = Bm25IndexBuilder.ShardIndexFor("MoveLinear", 64);
        Assert.Equal(first, second);
    }

    [Fact]
    public void ShardIndexForSpreadsTermsAcrossBuckets()
    {
        // 1000 distinct strings should hit "many" buckets — not all 64,
        // but well above a tiny handful. Demonstrates the hash isn't
        // pathologically collapsing.
        var hits = new HashSet<int>();
        for(var i = 0; i < 1000; i++)
            hits.Add(Bm25IndexBuilder.ShardIndexFor($"term{i}", 64));

        Assert.True(hits.Count > 30, $"expected > 30 unique buckets, got {hits.Count}");
    }

    [Fact]
    public void StatsShardCountReflectsRequestedShardCount()
    {
        var chunks = ManyChunks();
        var build = Bm25IndexBuilder.Build("lib", "1.0", chunks, shardCount: 32);

        Assert.Equal(32, build.Stats.ShardCount);
    }

    [Fact]
    public void StatsDocumentCountAndAverageDocLengthAreSane()
    {
        var chunks = new[]
        {
            MakeChunk("a", "MoveLinear axis homing"),
            MakeChunk("b", "Configure encoder feedback signal")
        };

        var build = Bm25IndexBuilder.Build("lib", "1.0", chunks);

        Assert.Equal(2, build.Stats.DocumentCount);
        Assert.True(build.Stats.AverageDocLength > 0);
    }

    private static IReadOnlyList<DocChunk> ManyChunks() =>
    [
        MakeChunk("a", "MoveLinear axis homing on each axis."),
        MakeChunk("b", "Configure encoder feedback for the controller."),
        MakeChunk("c", "AxisFault.Disabled is the cleared state of the latch."),
        MakeChunk("d", "ServoLoopGain controls the response of the loop."),
        MakeChunk("e", "TaskState transitions to Idle when the program completes.")
    ];

    private static DocChunk MakeChunk(string id, string content) =>
        new()
        {
            Id = id,
            LibraryId = "lib",
            Version = "1.0",
            PageUrl = "https://example.com",
            PageTitle = "Page",
            Category = DocCategory.ApiReference,
            Content = content
        };
}
