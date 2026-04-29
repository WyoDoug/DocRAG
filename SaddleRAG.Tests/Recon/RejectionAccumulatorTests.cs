// RejectionAccumulatorTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Ingestion.Recon;
using SaddleRAG.Ingestion.Symbols;

#endregion

namespace SaddleRAG.Tests.Recon;

public sealed class RejectionAccumulatorTests
{
    [Fact]
    public void AggregatesChunkCountAcrossChunks()
    {
        var acc = new RejectionAccumulator("lib", "1.0", totalChunks: 3);

        acc.Record(new RejectedToken { Name = "along", Reason = SymbolRejectionReason.NoStructureSignal },
                   chunkIndex: 0,
                   chunkContent: "first along sentence here");
        acc.Record(new RejectedToken { Name = "along", Reason = SymbolRejectionReason.NoStructureSignal },
                   chunkIndex: 1,
                   chunkContent: "second along sentence here");
        acc.Record(new RejectedToken { Name = "along", Reason = SymbolRejectionReason.NoStructureSignal },
                   chunkIndex: 2,
                   chunkContent: "third along sentence here");

        var built = acc.Build();
        var entry = Assert.Single(built, e => e.Name == "along");
        Assert.Equal(3, entry.ChunkCount);
    }

    [Fact]
    public void CapturesUpToThreeSamplesAcrossThirds()
    {
        var acc = new RejectionAccumulator("lib", "1.0", totalChunks: 30);

        acc.Record(new RejectedToken { Name = "noise", Reason = SymbolRejectionReason.NoStructureSignal },
                   chunkIndex: 0,
                   chunkContent: "first noise occurrence");
        acc.Record(new RejectedToken { Name = "noise", Reason = SymbolRejectionReason.NoStructureSignal },
                   chunkIndex: 15,
                   chunkContent: "middle noise occurrence");
        acc.Record(new RejectedToken { Name = "noise", Reason = SymbolRejectionReason.NoStructureSignal },
                   chunkIndex: 29,
                   chunkContent: "last noise occurrence");

        var built = acc.Build();
        var entry = Assert.Single(built, e => e.Name == "noise");
        Assert.Equal(3, entry.SampleSentences.Count);
        Assert.Contains(entry.SampleSentences, s => s.Contains("first"));
        Assert.Contains(entry.SampleSentences, s => s.Contains("middle"));
        Assert.Contains(entry.SampleSentences, s => s.Contains("last"));
    }

    [Fact]
    public void OnlySamplesOncePerThird()
    {
        var acc = new RejectionAccumulator("lib", "1.0", totalChunks: 30);

        for (int i = 0; i < 6; i++)
        {
            acc.Record(new RejectedToken { Name = "noise", Reason = SymbolRejectionReason.NoStructureSignal },
                       chunkIndex: i,
                       chunkContent: $"chunk {i} content with noise inside");
        }

        var built = acc.Build();
        var entry = Assert.Single(built, e => e.Name == "noise");
        Assert.Single(entry.SampleSentences);
        Assert.Contains("chunk 0", entry.SampleSentences[0]);
    }

    [Fact]
    public void TwoOccurrencesProduceTwoSamples()
    {
        var acc = new RejectionAccumulator("lib", "1.0", totalChunks: 10);

        acc.Record(new RejectedToken { Name = "noise", Reason = SymbolRejectionReason.NoStructureSignal },
                   chunkIndex: 0,
                   chunkContent: "alpha noise here");
        acc.Record(new RejectedToken { Name = "noise", Reason = SymbolRejectionReason.NoStructureSignal },
                   chunkIndex: 9,
                   chunkContent: "omega noise here");

        var built = acc.Build();
        var entry = Assert.Single(built, e => e.Name == "noise");
        Assert.Equal(2, entry.SampleSentences.Count);
    }

    [Fact]
    public void FirstSeenReasonWinsOnConflictingReports()
    {
        var acc = new RejectionAccumulator("lib", "1.0", totalChunks: 2);

        acc.Record(new RejectedToken { Name = "tok", Reason = SymbolRejectionReason.LibraryStoplist },
                   chunkIndex: 0,
                   chunkContent: "first tok use");
        acc.Record(new RejectedToken { Name = "tok", Reason = SymbolRejectionReason.NoStructureSignal },
                   chunkIndex: 1,
                   chunkContent: "second tok use");

        var built = acc.Build();
        var entry = Assert.Single(built, e => e.Name == "tok");
        Assert.Equal(SymbolRejectionReason.LibraryStoplist, entry.Reason);
    }

    [Fact]
    public void IdAndCapturedUtcArePopulated()
    {
        var acc = new RejectionAccumulator("aerotech-aeroscript", "1.0", totalChunks: 1);

        acc.Record(new RejectedToken { Name = "along", Reason = SymbolRejectionReason.NoStructureSignal },
                   chunkIndex: 0,
                   chunkContent: "axis moves along the path.");

        var built = acc.Build();
        var entry = Assert.Single(built);
        Assert.Equal("aerotech-aeroscript/1.0/along", entry.Id);
        Assert.True((DateTime.UtcNow - entry.CapturedUtc).TotalSeconds < 5);
    }

    [Fact]
    public void DropsNullSamplesFromMissingTokens()
    {
        // Defensive path — chunkContent doesn't contain the token (should
        // not happen in production, but accumulator must not crash). The
        // entry is still recorded but with no samples for that chunk.
        var acc = new RejectionAccumulator("lib", "1.0", totalChunks: 1);

        acc.Record(new RejectedToken { Name = "missing", Reason = SymbolRejectionReason.NoStructureSignal },
                   chunkIndex: 0,
                   chunkContent: "this content does not contain the token");

        var built = acc.Build();
        var entry = Assert.Single(built);
        Assert.Equal(1, entry.ChunkCount);
        Assert.Empty(entry.SampleSentences);
    }
}
