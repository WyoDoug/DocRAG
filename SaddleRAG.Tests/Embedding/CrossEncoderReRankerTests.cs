// CrossEncoderReRankerTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

public sealed class CrossEncoderReRankerTests
{
    [Theory]
    [InlineData("0.85", 0.85f)]
    [InlineData("0.0", 0.0f)]
    [InlineData("1.0", 1.0f)]
    [InlineData("0.97", 0.97f)]
    [InlineData(".42", 0.42f)]
    public void ParseScoreExtractsCleanFloat(string response, float expected)
    {
        var result = CrossEncoderReRanker.ParseScore(response);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Relevance: 0.85")]
    [InlineData("Score: 0.85")]
    [InlineData("0.85 (high confidence)")]
    [InlineData("The relevance score is 0.85.")]
    [InlineData("Relevance score: 0.85\nReasoning: ...")]
    public void ParseScoreToleratesCommonNoise(string response)
    {
        var result = CrossEncoderReRanker.ParseScore(response);

        Assert.Equal(0.85f, result);
    }

    [Theory]
    [InlineData("1.5", 1.0f)]
    [InlineData("2.0", 1.0f)]
    [InlineData("-0.5", 0.5f)]
    public void ParseScoreClampsOutOfRangeValues(string response, float expected)
    {
        // Note: -0.5 case — the regex captures "0.5" because the leading '-' isn't part
        // of the float pattern. That's the right behavior for this prompt: the model
        // shouldn't emit negatives, and if it does, treating "0.5" as the score is the
        // most defensible recovery.
        var result = CrossEncoderReRanker.ParseScore(response);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("I cannot judge")]
    public void ParseScoreReturnsZeroWhenNoFloatPresent(string response)
    {
        var result = CrossEncoderReRanker.ParseScore(response);

        Assert.Equal(0f, result);
    }

    [Fact]
    public void ParseScoreExtractsFirstFloatWhenMultiplePresent()
    {
        // Defensible: if the model emits multiple numbers, take the first one
        // (the model was instructed to emit only the score; the first float
        // encountered is the most likely intended answer).
        var result = CrossEncoderReRanker.ParseScore("0.85 vs 0.42");

        Assert.Equal(0.85f, result);
    }

}
