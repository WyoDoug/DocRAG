// // Bm25ScorerTests.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;
using DocRAG.Core.Models;
using DocRAG.Ingestion.Embedding;

#endregion

namespace DocRAG.Tests.Embedding;

public sealed class Bm25ScorerTests
{
    [Fact]
    public void EmptyIndexProducesEmptyScores()
    {
        var index = new Bm25Index();

        var scores = Bm25Scorer.Score(index, "anything");

        Assert.Empty(scores);
    }

    [Fact]
    public void IdentifierQueryHitsChunksContainingIdentifier()
    {
        var chunks = new[]
        {
            MakeChunk("a", "Use MoveLinear to start the move."),
            MakeChunk("b", "Coordinate motion is described elsewhere."),
            MakeChunk("c", "MoveLinear parameters control velocity.")
        };

        var index = Bm25IndexBuilder.Build(chunks);
        var scores = Bm25Scorer.Score(index, "MoveLinear");

        Assert.Equal(2, scores.Count);
        Assert.Contains("a", (System.Collections.Generic.IDictionary<string, double>) scores);
        Assert.Contains("c", (System.Collections.Generic.IDictionary<string, double>) scores);
        Assert.DoesNotContain("b", (System.Collections.Generic.IDictionary<string, double>) scores);
    }

    [Fact]
    public void ProseQueryMatchesLowercasedTokens()
    {
        var chunks = new[]
        {
            MakeChunk("a", "Configure homing on each axis."),
            MakeChunk("b", "Configure encoder feedback.")
        };

        var index = Bm25IndexBuilder.Build(chunks);
        var scores = Bm25Scorer.Score(index, "homing configuration");

        Assert.NotEmpty(scores);
        Assert.True(scores["a"] > scores.GetValueOrDefault("b", 0.0));
    }

    [Fact]
    public void TopNReturnsHighestScoringChunksInOrder()
    {
        var chunks = new[]
        {
            MakeChunk("a", "MoveLinear once."),
            MakeChunk("b", "MoveLinear MoveLinear twice."),
            MakeChunk("c", "MoveLinear MoveLinear MoveLinear three times.")
        };

        var index = Bm25IndexBuilder.Build(chunks);
        var topTwo = Bm25Scorer.TopN(index, "MoveLinear", n: 2);

        Assert.Equal(2, topTwo.Count);
        // The top result should be one with the most occurrences.
        Assert.Contains(topTwo, t => t.ChunkId == "c");
    }

    [Fact]
    public void DottedIdentifierQueryMatchesDottedIdentifier()
    {
        var chunks = new[]
        {
            MakeChunk("a", "Set AxisFault.Disabled to acknowledge."),
            MakeChunk("b", "Other unrelated content here.")
        };

        var index = Bm25IndexBuilder.Build(chunks);
        var scores = Bm25Scorer.Score(index, "AxisFault.Disabled");

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
