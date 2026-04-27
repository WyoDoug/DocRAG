// // CategoryAwareChunkerTests.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;
using DocRAG.Core.Models;
using DocRAG.Ingestion.Chunking;
using DocRAG.Ingestion.Symbols;

#endregion

namespace DocRAG.Tests.Chunking;

public sealed class CategoryAwareChunkerTests
{
    [Fact]
    public void SplitToCharLimitDoesNotBreakInsideDottedIdentifier()
    {
        // Build a page whose content forces SplitToCharLimit to fire (over the
        // 2400-char chunk size limit) and whose only "." in the back half
        // of the split window is INSIDE a dotted identifier (AxisFault.Disabled).
        // The pre-fix behavior splits between AxisFault and Disabled.
        var filler = new string('a', 1300) + " ";
        var content = filler + "Set AxisFault.Disabled to acknowledge the latch and continue. " + filler;
        var page = MakePage(content, DocCategory.HowTo);

        var chunker = new CategoryAwareChunker(new SymbolExtractor());
        var chunks = chunker.Chunk(page);

        Assert.True(chunks.Count >= 2, "expected the over-limit content to split into multiple chunks");
        foreach(var chunk in chunks)
        {
            Assert.False(chunk.Content.EndsWith("AxisFault.", StringComparison.Ordinal),
                         $"chunk should not end mid-dotted-identifier: '{chunk.Content[^60..]}'");
            Assert.False(chunk.Content.StartsWith("Disabled", StringComparison.Ordinal),
                         $"chunk should not start with the leaf of a cut identifier: '{chunk.Content[..30]}'");
        }
    }

    [Fact]
    public void SplitToCharLimitBreaksAtRealSentenceEnd()
    {
        // Force SplitToCharLimit to fire and ensure that a real sentence-end
        // period (followed by whitespace) IS used as a break point.
        var filler = new string('b', 1300);
        var sentenceEnd = "End of first sentence. Beginning of second sentence.";
        var content = filler + " " + sentenceEnd + " " + filler;
        var page = MakePage(content, DocCategory.HowTo);

        var chunker = new CategoryAwareChunker(new SymbolExtractor());
        var chunks = chunker.Chunk(page);

        Assert.True(chunks.Count >= 2, "expected the over-limit content to split into multiple chunks");
        var firstHasSentenceTerminator = chunks[0].Content.EndsWith(".", StringComparison.Ordinal);
        Assert.True(firstHasSentenceTerminator,
                    $"first chunk should end at a sentence terminator: '{chunks[0].Content[^30..]}'");
    }

    private static PageRecord MakePage(string content, DocCategory category) =>
        new()
        {
            Id = "test-page",
            LibraryId = "test-lib",
            Version = "1.0",
            Url = "https://example.com/test",
            Title = "Test Page",
            Category = category,
            RawContent = content,
            FetchedAt = DateTime.UtcNow,
            ContentHash = "test-hash"
        };
}
