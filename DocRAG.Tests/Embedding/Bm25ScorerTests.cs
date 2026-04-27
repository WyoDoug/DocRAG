// Bm25ScorerTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;
using DocRAG.Core.Models;
using DocRAG.Ingestion.Embedding;

#endregion

namespace DocRAG.Tests.Embedding;

public sealed class Bm25ScorerTests
{
    [Fact]
    public async Task EmptyIndexProducesEmptyScores()
    {
        var build = Bm25IndexBuilder.Build("lib", "1.0", []);
        var lookup = new InMemoryBm25TermLookup(build);

        var scores = await Bm25Scorer.ScoreAsync(lookup, build.Stats, "anything");

        Assert.Empty(scores);
    }

    [Fact]
    public async Task IdentifierQueryHitsChunksContainingIdentifier()
    {
        var chunks = new[]
        {
            MakeChunk("a", "Use MoveLinear to start the move."),
            MakeChunk("b", "Coordinate motion is described elsewhere."),
            MakeChunk("c", "MoveLinear parameters control velocity.")
        };

        var build = Bm25IndexBuilder.Build("lib", "1.0", chunks);
        var lookup = new InMemoryBm25TermLookup(build);
        var scores = await Bm25Scorer.ScoreAsync(lookup, build.Stats, "MoveLinear");

        Assert.Equal(2, scores.Count);
        Assert.True(scores.ContainsKey("a"));
        Assert.True(scores.ContainsKey("c"));
        Assert.False(scores.ContainsKey("b"));
    }

    [Fact]
    public async Task ProseQueryMatchesLowercasedTokens()
    {
        var chunks = new[]
        {
            MakeChunk("a", "Configure homing on each axis."),
            MakeChunk("b", "Configure encoder feedback.")
        };

        var build = Bm25IndexBuilder.Build("lib", "1.0", chunks);
        var lookup = new InMemoryBm25TermLookup(build);
        var scores = await Bm25Scorer.ScoreAsync(lookup, build.Stats, "homing configuration");

        Assert.NotEmpty(scores);
        Assert.True(scores["a"] > scores.GetValueOrDefault("b", 0.0));
    }

    [Fact]
    public async Task TopNReturnsHighestScoringChunksInOrder()
    {
        var chunks = new[]
        {
            MakeChunk("a", "MoveLinear once."),
            MakeChunk("b", "MoveLinear MoveLinear twice."),
            MakeChunk("c", "MoveLinear MoveLinear MoveLinear three times.")
        };

        var build = Bm25IndexBuilder.Build("lib", "1.0", chunks);
        var lookup = new InMemoryBm25TermLookup(build);
        var topTwo = await Bm25Scorer.TopNAsync(lookup, build.Stats, "MoveLinear", n: 2);

        Assert.Equal(2, topTwo.Count);
        Assert.Contains(topTwo, t => t.ChunkId == "c");
    }

    [Fact]
    public async Task DottedIdentifierQueryMatchesDottedIdentifier()
    {
        var chunks = new[]
        {
            MakeChunk("a", "Set AxisFault.Disabled to acknowledge."),
            MakeChunk("b", "Other unrelated content here.")
        };

        var build = Bm25IndexBuilder.Build("lib", "1.0", chunks);
        var lookup = new InMemoryBm25TermLookup(build);
        var scores = await Bm25Scorer.ScoreAsync(lookup, build.Stats, "AxisFault.Disabled");

        Assert.NotEmpty(scores);
        Assert.True(scores.ContainsKey("a"));
    }

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
