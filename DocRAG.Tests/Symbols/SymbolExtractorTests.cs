// // SymbolExtractorTests.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;
using DocRAG.Core.Models;
using DocRAG.Ingestion.Symbols;

#endregion

namespace DocRAG.Tests.Symbols;

public sealed class SymbolExtractorTests
{
    [Fact]
    public void DroppsEnglishStopwordPickedFromProse()
    {
        var profile = MakeProfile([]);
        var extractor = new SymbolExtractor();

        var result = extractor.Extract("When the axis homes, the marker is hit.", profile);

        Assert.DoesNotContain(result.Symbols, s => s.Name.Equals("When", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Symbols, s => s.Name.Equals("the", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void KeepsLikelySymbolEvenWithNoOtherSignal()
    {
        var profile = MakeProfile(["MoveLinear"]);
        var extractor = new SymbolExtractor();

        var result = extractor.Extract("The MoveLinear command moves the axis.", profile);

        Assert.Contains(result.Symbols, s => s.Name == "MoveLinear");
    }

    [Fact]
    public void KeepsDeclaredClassEvenWithoutLikelySymbolsBoost()
    {
        var profile = MakeProfile([]);
        var extractor = new SymbolExtractor();

        var result = extractor.Extract("class Controller : IDisposable { }", profile);

        var symbol = Assert.Single(result.Symbols, s => s.Name == "Controller");
        Assert.Equal(SymbolKind.Type, symbol.Kind);
    }

    [Fact]
    public void ClassifiesEnumDeclarationAsEnumKind()
    {
        var profile = MakeProfile([]);
        var extractor = new SymbolExtractor();

        var result = extractor.Extract("enum HomeType { ToLimit, ToMarker }", profile);

        var symbol = Assert.Single(result.Symbols, s => s.Name == "HomeType");
        Assert.Equal(SymbolKind.Enum, symbol.Kind);
    }

    [Fact]
    public void ClassifiesCallableShapeAsFunction()
    {
        var profile = MakeProfile(["MoveLinear"]);
        var extractor = new SymbolExtractor();

        var result = extractor.Extract("Invoke MoveLinear(axis, distance) to start the move.", profile);

        var symbol = Assert.Single(result.Symbols, s => s.Name == "MoveLinear");
        Assert.Equal(SymbolKind.Function, symbol.Kind);
    }

    [Fact]
    public void ClassifiesDottedMemberAsProperty()
    {
        var profile = MakeProfile(["AxisFault"]);
        var extractor = new SymbolExtractor();

        var result = extractor.Extract("Set AxisFault.Disabled to acknowledge.", profile);

        var symbol = Assert.Single(result.Symbols, s => s.Name == "AxisFault.Disabled");
        Assert.Equal(SymbolKind.Property, symbol.Kind);
        Assert.Equal("AxisFault", symbol.Container);
    }

    [Fact]
    public void DropsSingleCharacterCandidate()
    {
        var profile = MakeProfile([]);
        var extractor = new SymbolExtractor();

        var result = extractor.Extract("a class is one of N classes.", profile);

        Assert.DoesNotContain(result.Symbols, s => s.Name.Length < 2);
    }

    [Fact]
    public void DoesNotEmitTrailingPeriodArtifact()
    {
        var profile = MakeProfile(["AxisFault"]);
        var extractor = new SymbolExtractor();

        var result = extractor.Extract("Refer to the AxisFault. enumeration for the list of values.", profile);

        Assert.DoesNotContain(result.Symbols, s => s.Name.EndsWith(".", StringComparison.Ordinal));
        Assert.Contains(result.Symbols, s => s.Name == "AxisFault");
    }

    [Fact]
    public void PrimaryQualifiedNamePrefersTypeOverFunction()
    {
        var profile = MakeProfile(["Controller", "MoveLinear"]);
        var extractor = new SymbolExtractor();

        var result = extractor.Extract("class Controller { MoveLinear() { } }", profile);

        Assert.Equal("Controller", result.PrimaryQualifiedName);
    }

    [Fact]
    public void EmptyProfileStillKeepsStructuredIdentifiers()
    {
        // Even without a profile, internally-structured identifiers (snake_case,
        // dotted, callable, generic, declared) should survive — that's the point
        // of rule 5. This is what allows first-pass ingestion to produce useful
        // Symbols[] before recon runs.
        var profile = MakeProfile([]);
        var extractor = new SymbolExtractor();

        var result = extractor.Extract("class Controller { } enum HomeType { } void move_linear() {}", profile);

        Assert.Contains(result.Symbols, s => s.Name == "Controller");
        Assert.Contains(result.Symbols, s => s.Name == "HomeType");
        Assert.Contains(result.Symbols, s => s.Name == "move_linear");
    }

    [Fact]
    public void CodeFenceCorpusContextRescuesProseOnlyMention()
    {
        var profile = MakeProfile([]);
        var extractor = new SymbolExtractor();
        var corpus = new CorpusContext { CodeFenceSymbols = new HashSet<string> { "AutofocusSetup" } };

        var result = extractor.Extract("The AutofocusSetup parameter controls the loop.", profile, corpus);

        Assert.Contains(result.Symbols, s => s.Name == "AutofocusSetup");
    }

    [Fact]
    public void ProseMentionThresholdRescuesFrequentProseMention()
    {
        var profile = MakeProfile([]);
        var extractor = new SymbolExtractor(proseMentionThreshold: 3);
        var corpus = new CorpusContext { ProseMentionCounts = new Dictionary<string, int> { ["AutofocusSetup"] = 5 } };

        var result = extractor.Extract("The AutofocusSetup parameter controls the loop.", profile, corpus);

        Assert.Contains(result.Symbols, s => s.Name == "AutofocusSetup");
    }

    private static LibraryProfile MakeProfile(IReadOnlyList<string> likelySymbols)
    {
        var result = new LibraryProfile
                         {
                             Id = "test-lib/1.0",
                             LibraryId = "test-lib",
                             Version = "1.0",
                             Source = "test",
                             LikelySymbols = likelySymbols
                         };
        return result;
    }
}
