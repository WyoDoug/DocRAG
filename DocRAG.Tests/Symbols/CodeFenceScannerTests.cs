// // CodeFenceScannerTests.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using DocRAG.Ingestion.Symbols;

#endregion

namespace DocRAG.Tests.Symbols;

public sealed class CodeFenceScannerTests
{
    [Fact]
    public void ExtractsIdentifiersFromTripleBacktickFence()
    {
        var content = "Some prose.\n```csharp\nvar c = new Controller();\nc.MoveLinear(1.0);\n```\nMore prose.";

        var symbols = CodeFenceScanner.ScanContents([content]);

        Assert.Contains("Controller", symbols);
        Assert.Contains("MoveLinear", symbols);
    }

    [Fact]
    public void ExtractsIdentifiersFromPreCodeBlock()
    {
        var content = "<p>Prose.</p><pre><code>AxisStatus s = controller.GetAxisStatus(0);</code></pre><p>More.</p>";

        var symbols = CodeFenceScanner.ScanContents([content]);

        Assert.Contains("AxisStatus", symbols);
        Assert.Contains("GetAxisStatus", symbols);
    }

    [Fact]
    public void DoesNotPickUpIdentifiersOutsideFences()
    {
        var content = "Foo is mentioned only in prose. There is no code fence here.";

        var symbols = CodeFenceScanner.ScanContents([content]);

        Assert.DoesNotContain("Foo", symbols);
    }

    [Fact]
    public void DropsSingleCharacterCandidates()
    {
        var content = "```\na b c X\n```";

        var symbols = CodeFenceScanner.ScanContents([content]);

        Assert.Empty(symbols);
    }

    [Fact]
    public void HandlesEmptyInput()
    {
        var symbols = CodeFenceScanner.ScanContents(Array.Empty<string>());

        Assert.Empty(symbols);
    }

    [Fact]
    public void DedupesAcrossMultipleChunks()
    {
        var contents = new[]
        {
            "```\nMoveLinear(0);\n```",
            "```\nMoveLinear(1);\n```"
        };

        var symbols = CodeFenceScanner.ScanContents(contents);

        Assert.Single(symbols, s => s == "MoveLinear");
    }
}
