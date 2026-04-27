// SampleWindowExtractorTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Ingestion.Symbols;

#endregion

namespace DocRAG.Tests.Symbols;

public sealed class SampleWindowExtractorTests
{
    [Fact]
    public void ExtractsWindowAroundFirstOccurrence()
    {
        var content = "The MoveLinear command moves the axis to the target position.";

        var sample = SampleWindowExtractor.Extract(content, "MoveLinear");

        Assert.NotNull(sample);
        Assert.Contains("MoveLinear", sample);
    }

    [Fact]
    public void CapsTotalLengthAt200Characters()
    {
        var prefix = new string('a', 500);
        var suffix = new string('b', 500);
        var content = $"{prefix} TOKEN {suffix}";

        var sample = SampleWindowExtractor.Extract(content, "TOKEN");

        Assert.NotNull(sample);
        Assert.True(sample.Length <= 200, $"sample length was {sample.Length}, expected <= 200");
        Assert.Contains("TOKEN", sample);
    }

    [Fact]
    public void TrimsToWordBoundaries()
    {
        // Long prefix so the window's left edge falls inside actual content
        // (not pinned to position 0). The edge should sit at a space, so
        // the sample starts with a complete "prefixNN" word, not a partial
        // fragment like "fix37".
        var prefix = string.Join(" ", Enumerable.Range(0, 50).Select(i => $"prefix{i:D2}"));
        var content = $"{prefix} Symbol suffix";

        var sample = SampleWindowExtractor.Extract(content, "Symbol");

        Assert.NotNull(sample);
        Assert.Matches(@"^prefix\d{2}", sample);
    }

    [Fact]
    public void TokenAtStartOfChunkHasNoLeftContext()
    {
        var content = "MoveLinear is the entrypoint for axis motion.";

        var sample = SampleWindowExtractor.Extract(content, "MoveLinear");

        Assert.NotNull(sample);
        Assert.StartsWith("MoveLinear", sample);
    }

    [Fact]
    public void TokenAtEndOfChunkHasNoRightContext()
    {
        var content = "The entrypoint for axis motion is MoveLinear";

        var sample = SampleWindowExtractor.Extract(content, "MoveLinear");

        Assert.NotNull(sample);
        Assert.EndsWith("MoveLinear", sample);
    }

    [Fact]
    public void ReturnsFirstOccurrenceWhenTokenAppearsMultipleTimes()
    {
        var content = "First MoveLinear, then later MoveLinear, and again MoveLinear.";

        var sample = SampleWindowExtractor.Extract(content, "MoveLinear");

        Assert.NotNull(sample);
        Assert.StartsWith("First MoveLinear", sample);
    }

    [Fact]
    public void ReturnsNullWhenTokenIsNotPresent()
    {
        var content = "The axis homes to the marker.";

        var sample = SampleWindowExtractor.Extract(content, "MissingToken");

        Assert.Null(sample);
    }

    [Fact]
    public void CollapsesInternalWhitespaceToSingleSpaces()
    {
        var content = "Use   MoveLinear\n\nto\tmove   the    axis.";

        var sample = SampleWindowExtractor.Extract(content, "MoveLinear");

        Assert.NotNull(sample);
        Assert.DoesNotContain("\n", sample);
        Assert.DoesNotContain("\t", sample);
        Assert.DoesNotContain("  ", sample);
    }
}
