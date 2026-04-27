// // ChunkBoundaryAuditTests.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;
using DocRAG.Core.Models;
using DocRAG.Ingestion.Symbols;

#endregion

namespace DocRAG.Tests.Symbols;

public sealed class ChunkBoundaryAuditTests
{
    [Fact]
    public void IgnoresLegitimateSentenceEnd()
    {
        // Two adjacent chunks where the first ends with a normal sentence-end
        // period and the second starts with a fresh sentence. No dotted
        // identifier of the form "configured.Disabled" exists anywhere in
        // the corpus, so this is NOT a chunker cut.
        var chunks = new[]
        {
            MakeChunk("page-a", 0, "The motor is correctly configured."),
            MakeChunk("page-a", 1, "Disabled axes will not respond to motion commands.")
        };

        var count = ChunkBoundaryAudit.CountIssues(chunks);

        Assert.Equal(0, count);
    }

    [Fact]
    public void DetectsRealCutWhenJoinMatchesCorpusDottedIdentifier()
    {
        // Chunk 0 ends with "AxisFault." and chunk 1 starts with "Disabled".
        // A separate chunk in the same corpus mentions "AxisFault.Disabled" —
        // confirming the join is a known dotted identifier. The chunker cut
        // it in half.
        var chunks = new[]
        {
            MakeChunk("page-a", 0, "Refer to the AxisFault."),
            MakeChunk("page-a", 1, "Disabled state is the cleared latch."),
            MakeChunk("page-b", 0, "Set AxisFault.Disabled to acknowledge the latch.")
        };

        var count = ChunkBoundaryAudit.CountIssues(chunks);

        Assert.Equal(1, count);
    }

    [Fact]
    public void DetectsCutInLongerDottedPathViaPairwiseSegment()
    {
        // Corpus contains "Foo.Bar.Baz" so both "Foo.Bar" and "Bar.Baz" should
        // be recognized as cut targets via pairwise-segment expansion.
        var chunks = new[]
        {
            MakeChunk("page-a", 0, "Use Foo.Bar."),
            MakeChunk("page-a", 1, "Baz to do the thing."),
            MakeChunk("page-b", 0, "The full path Foo.Bar.Baz is documented here.")
        };

        var count = ChunkBoundaryAudit.CountIssues(chunks);

        Assert.Equal(1, count);
    }

    [Fact]
    public void DetectsLeadingDotChunkWhenPrevExists()
    {
        // A non-first chunk that starts with "." is a hard cut signal —
        // there's a predecessor in the same page, so the period almost
        // certainly belongs to a Foo.Leaf identifier the chunker split.
        var chunks = new[]
        {
            MakeChunk("page-a", 0, "Some content here"),
            MakeChunk("page-a", 1, ".Disabled state is the cleared latch.")
        };

        var count = ChunkBoundaryAudit.CountIssues(chunks);

        Assert.Equal(1, count);
    }

    [Fact]
    public void IgnoresLeadingDotInFirstChunkOfPage()
    {
        // ".NET", ".gitignore", ".htaccess" etc. legitimately appear at the
        // start of the first chunk of a page (the page is ABOUT them).
        // Without a predecessor in the same page, leading-dot is content,
        // not a chunker cut, and must not be counted.
        var chunks = new[]
        {
            MakeChunk("page-a", 0, ".NET API Guidelines for the Automation1 controller."),
            MakeChunk("page-b", 0, ".gitignore syntax explained.")
        };

        var count = ChunkBoundaryAudit.CountIssues(chunks);

        Assert.Equal(0, count);
    }

    [Fact]
    public void DoesNotDoubleCountWhenCutAndCorpusEvidenceCoincide()
    {
        // The cut itself is the only evidence of "AxisFault.Disabled" in the
        // corpus. No other chunk mentions the joined form. Without external
        // confirmation the audit should NOT count this — we have no
        // independent evidence that the join was a real identifier.
        var chunks = new[]
        {
            MakeChunk("page-a", 0, "Refer to the AxisFault."),
            MakeChunk("page-a", 1, "Disabled state is the cleared latch.")
        };

        var count = ChunkBoundaryAudit.CountIssues(chunks);

        Assert.Equal(0, count);
    }

    [Fact]
    public void RespectsPageBoundaryWhenChecking()
    {
        // Chunk-0-of-page-a ends with "AxisFault." and chunk-0-of-page-b
        // starts with "Disabled". They are NOT adjacent (different pages),
        // so even though "AxisFault.Disabled" appears elsewhere, the audit
        // must not count a cross-page false positive.
        var chunks = new[]
        {
            MakeChunk("page-a", 0, "Refer to the AxisFault."),
            MakeChunk("page-b", 0, "Disabled state is the cleared latch."),
            MakeChunk("page-c", 0, "Set AxisFault.Disabled to acknowledge.")
        };

        var count = ChunkBoundaryAudit.CountIssues(chunks);

        Assert.Equal(0, count);
    }

    [Fact]
    public void ReturnsZeroForEmptyInput()
    {
        var count = ChunkBoundaryAudit.CountIssues(Array.Empty<DocChunk>());

        Assert.Equal(0, count);
    }

    [Fact]
    public void OrdersChunksByIndexNotLexicographicallyWithinPage()
    {
        // Chunk indexes 9 and 10 sort lex as "10" < "9". The audit must use
        // numeric ordering so chunk 10 is treated as adjacent to chunk 9, not
        // chunk 1.
        var chunks = new[]
        {
            MakeChunk("page-a", 9, "Refer to the AxisFault."),
            MakeChunk("page-a", 10, "Disabled state is the cleared latch."),
            MakeChunk("page-a", 1, "Earlier content not relevant."),
            MakeChunk("page-b", 0, "Set AxisFault.Disabled to acknowledge.")
        };

        var count = ChunkBoundaryAudit.CountIssues(chunks);

        Assert.Equal(1, count);
    }

    private static DocChunk MakeChunk(string pageSlug, int index, string content) =>
        new()
        {
            Id = $"test-lib/1.0/{pageSlug}/{index}",
            LibraryId = "test-lib",
            Version = "1.0",
            PageUrl = $"https://example.com/{pageSlug}",
            PageTitle = pageSlug,
            Category = DocCategory.HowTo,
            Content = content,
            SectionPath = pageSlug
        };
}
