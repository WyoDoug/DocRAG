// SymbolExtractorTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

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

    [Theory]
    [InlineData("IMPORTANT")]
    [InlineData("HARDWARE")]
    [InlineData("RAM")]
    [InlineData("CPU")]
    [InlineData("BD")]
    [InlineData("TCP")]
    [InlineData("UTF")]
    public void DropsAllUppercaseShortTokenWhenOnlyStructureSignal(string token)
    {
        var profile = MakeProfile([]);
        var extractor = new SymbolExtractor();

        var result = extractor.Extract($"The {token} field stores the value.", profile);

        Assert.DoesNotContain(result.Symbols, s => s.Name == token);
    }

    [Theory]
    [InlineData("PIDController")]
    [InlineData("XMLParser")]
    [InlineData("IOError")]
    [InlineData("HTTPRequest")]
    public void KeepsPascalCaseCompoundStartingWithAcronym(string token)
    {
        var profile = MakeProfile([]);
        var extractor = new SymbolExtractor();

        var result = extractor.Extract($"Use {token} to handle the operation.", profile);

        Assert.Contains(result.Symbols, s => s.Name == token);
    }

    [Theory]
    [InlineData("MoveLinear")]
    [InlineData("EasyTune")]
    [InlineData("HyperWire")]
    [InlineData("MachineApps")]
    public void KeepsClassicCamelCaseCompound(string token)
    {
        var profile = MakeProfile([]);
        var extractor = new SymbolExtractor();

        var result = extractor.Extract($"Use {token} to do the thing.", profile);

        Assert.Contains(result.Symbols, s => s.Name == token);
    }

    [Fact]
    public void DropsAllUpperShortTokenEvenIfProseFrequent()
    {
        // RAM mentioned 5 times in prose corpus-wide. Without other signal
        // (likely-symbols, code-fence, declared, callable) it must NOT survive
        // — the prose-frequent rule is gated against likely-abbreviation tokens.
        var profile = MakeProfile([]);
        var extractor = new SymbolExtractor(proseMentionThreshold: 3);
        var corpus = new CorpusContext { ProseMentionCounts = new Dictionary<string, int> { ["RAM"] = 5 } };

        var result = extractor.Extract("The RAM stores the data.", profile, corpus);

        Assert.DoesNotContain(result.Symbols, s => s.Name == "RAM");
    }

    [Fact]
    public void KeepsAllUpperShortTokenIfInLikelySymbols()
    {
        // Per-library override: PSO is a real Aerotech symbol (Position
        // Synchronized Output). With it in LikelySymbols, the abbreviation
        // gate is bypassed.
        var profile = MakeProfile(["PSO"]);
        var extractor = new SymbolExtractor();

        var result = extractor.Extract("Configure the PSO output for the axis.", profile);

        Assert.Contains(result.Symbols, s => s.Name == "PSO");
    }

    [Theory]
    [InlineData("GHz")]
    [InlineData("MHz")]
    [InlineData("kHz")]
    [InlineData("RPM")]
    [InlineData("rpm")]
    [InlineData("psi")]
    [InlineData("dB")]
    [InlineData("kB")]
    [InlineData("MB")]
    public void DropsUnitAbbreviationByDefault(string unit)
    {
        var profile = MakeProfile([]);
        var extractor = new SymbolExtractor();

        var result = extractor.Extract($"The signal is 100 {unit} at peak.", profile);

        Assert.DoesNotContain(result.Symbols, s => s.Name == unit);
    }

    [Theory]
    [InlineData("IMPORTANT")]
    [InlineData("NOTE")]
    [InlineData("WARNING")]
    [InlineData("CAUTION")]
    [InlineData("BACK")]
    [InlineData("OK")]
    public void DropsDocCalloutWord(string word)
    {
        // Doc-callout words appear at the head of inline notes
        // ("IMPORTANT: do this") and tokenize as identifier-shaped. They
        // are stoplisted regardless of casing.
        var profile = MakeProfile([]);
        var extractor = new SymbolExtractor();

        var result = extractor.Extract($"{word}: configure the axis carefully before homing.", profile);

        Assert.DoesNotContain(result.Symbols, s => s.Name == word);
    }

    [Fact]
    public void RejectionReasonGlobalStoplist()
    {
        var profile = MakeProfile([]);
        var extractor = new SymbolExtractor();

        var result = extractor.Extract("The axis homes to the marker.", profile);

        Assert.Contains(result.Rejected, r => r.Name == "The" && r.Reason == SymbolRejectionReason.GlobalStoplist);
    }

    [Fact]
    public void RejectionReasonLibraryStoplist()
    {
        var profile = MakeProfileWithStoplist(["BrandX"]);
        var extractor = new SymbolExtractor();

        var result = extractor.Extract("Use BrandX hardware to drive the axis.", profile);

        Assert.Contains(result.Rejected, r => r.Name == "BrandX" && r.Reason == SymbolRejectionReason.LibraryStoplist);
        Assert.DoesNotContain(result.Symbols, s => s.Name == "BrandX");
    }

    [Fact]
    public void RejectionReasonUnit()
    {
        var profile = MakeProfile([]);
        var extractor = new SymbolExtractor();

        var result = extractor.Extract("The signal is 100 GHz at peak.", profile);

        Assert.Contains(result.Rejected, r => r.Name == "GHz" && r.Reason == SymbolRejectionReason.Unit);
    }

    [Fact]
    public void RejectionReasonBelowMinLength()
    {
        // "_" is a valid identifier-start character that tokenizes as a
        // single-char candidate. It is not in the global stoplist, so the
        // length check fires (MinIdentifierLength == 2) and BelowMinLength
        // is the reason.
        var profile = MakeProfile([]);
        var extractor = new SymbolExtractor();

        var result = extractor.Extract("The _ value is set.", profile);

        Assert.Contains(result.Rejected, r => r.Name == "_" && r.Reason == SymbolRejectionReason.BelowMinLength);
    }

    [Fact]
    public void RejectionReasonLikelyAbbreviation()
    {
        // RAM has prose mentions >= threshold but is short all-uppercase, so
        // IsLikelyAbbreviation blocks the prose-frequent rule. No other keep
        // signal applies -- the reason should be LikelyAbbreviation, NOT
        // NoStructureSignal.
        var profile = MakeProfile([]);
        var extractor = new SymbolExtractor(proseMentionThreshold: 3);
        var corpus = new CorpusContext { ProseMentionCounts = new Dictionary<string, int> { ["RAM"] = 5 } };

        var result = extractor.Extract("The RAM stores the data.", profile, corpus);

        Assert.Contains(result.Rejected, r => r.Name == "RAM" && r.Reason == SymbolRejectionReason.LikelyAbbreviation);
    }

    [Fact]
    public void RejectionReasonNoStructureSignal()
    {
        // "alongthing" is not in stoplist, not a unit, length OK, but has no
        // mid-word capital, no underscore, no callable shape, no container,
        // no prose-frequent mentions, not declared. NoStructureSignal.
        var profile = MakeProfile([]);
        var extractor = new SymbolExtractor();

        var result = extractor.Extract("alongthing.", profile);

        Assert.Contains(result.Rejected, r => r.Name == "alongthing" && r.Reason == SymbolRejectionReason.NoStructureSignal);
    }

    [Fact]
    public void LibraryStoplistOverridesLikelySymbols()
    {
        // If a token is in BOTH lists, stoplist wins (matches existing
        // extraction behavior -- Stoplist is a hard reject).
        var profile = new LibraryProfile
                          {
                              Id = "test-lib/1.0",
                              LibraryId = "test-lib",
                              Version = "1.0",
                              Source = "test",
                              LikelySymbols = ["Foo"],
                              Stoplist = ["Foo"]
                          };
        var extractor = new SymbolExtractor();

        var result = extractor.Extract("Configure the Foo widget.", profile);

        Assert.DoesNotContain(result.Symbols, s => s.Name == "Foo");
        Assert.Contains(result.Rejected, r => r.Name == "Foo" && r.Reason == SymbolRejectionReason.LibraryStoplist);
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

    private static LibraryProfile MakeProfileWithStoplist(IReadOnlyList<string> stoplist)
    {
        var result = new LibraryProfile
                         {
                             Id = "test-lib/1.0",
                             LibraryId = "test-lib",
                             Version = "1.0",
                             Source = "test",
                             Stoplist = stoplist
                         };
        return result;
    }
}
