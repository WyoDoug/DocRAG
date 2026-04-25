// // AeroScriptListClassesGoldenTest.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using System.Text.Json;
using DocRAG.Core.Enums;
using DocRAG.Core.Models;
using DocRAG.Ingestion.Symbols;

#endregion

namespace DocRAG.Tests.Integration;

/// <summary>
///     Regression seal for the parser fix. Loads representative
///     AeroScript-style chunks from a fixture and runs the new symbol
///     extractor against them. Asserts that the reviewer's specific
///     complaints — junk English words, trailing-period mentions,
///     single characters — no longer survive, while real types do.
/// </summary>
public sealed class AeroScriptListClassesGoldenTest
{
    [Fact]
    public void NewExtractorEmitsRealTypesAndDropsJunk()
    {
        var chunks = LoadChunks();
        var profile = MakeAeroScriptProfile();
        var extractor = new SymbolExtractor();

        var allSymbols = chunks
                         .SelectMany(content => extractor.Extract(content, profile).Symbols)
                         .ToList();

        var typeNames = allSymbols
                        .Where(s => s.Kind == SymbolKind.Type || s.Kind == SymbolKind.Enum)
                        .Select(s => s.Name)
                        .ToHashSet(StringComparer.Ordinal);

        var allNames = allSymbols.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

        // Real types must be present.
        AssertContains(typeNames, "MoveLinear");
        AssertContains(typeNames, "AxisFault");
        AssertContains(typeNames, "HomeType");
        AssertContains(typeNames, "RampMode");
        AssertContains(typeNames, "Controller");
        AssertContains(typeNames, "AxisStatus");
        AssertContains(typeNames, "ServoLoopGain");
        AssertContains(typeNames, "TaskState");
        AssertContains(typeNames, "AnalogOutputUpdateEvent");

        // Junk English words must be absent.
        AssertAbsent(allNames, "When");
        AssertAbsent(allNames, "Each");
        AssertAbsent(allNames, "Returns");
        AssertAbsent(allNames, "Values");
        AssertAbsent(allNames, "For");
        AssertAbsent(allNames, "Use");
        AssertAbsent(allNames, "Represents");
        AssertAbsent(allNames, "the");
        AssertAbsent(allNames, "and");
        AssertAbsent(allNames, "with");

        // Trailing-period artifacts must be absent.
        var trailingPeriod = allNames.Where(n => n.EndsWith('.')).ToList();
        Assert.True(trailingPeriod.Count == 0,
                    $"No symbol should end with '.', found: {string.Join(", ", trailingPeriod)}");

        // Single-character symbols must be absent.
        var singleChar = allNames.Where(n => n.Length < 2).ToList();
        Assert.True(singleChar.Count == 0,
                    $"No symbol should be a single character, found: {string.Join(", ", singleChar)}");
    }

    [Fact]
    public void EmptyLikelySymbolsCalibrationKeepsKnownSymbolsAlive()
    {
        // The reviewer worried that recon might return a confident-but-partial
        // LikelySymbols list, and the keep rules might be too strict to recover
        // misses. This test verifies that with LikelySymbols deliberately empty,
        // at least 80% of the known AeroScript symbols still survive via the
        // corpus-context rules (declared form, code fence, dotted member,
        // internal structure).
        var chunks = LoadChunks();
        var knownSymbols = LoadKnownSymbols();

        var profile = MakeAeroScriptProfile() with { LikelySymbols = [] };
        var extractor = new SymbolExtractor();

        var allNames = chunks
                       .SelectMany(content => extractor.Extract(content, profile).Symbols)
                       .Select(s => s.Name)
                       .ToHashSet(StringComparer.Ordinal);

        var survived = knownSymbols.Where(allNames.Contains).ToList();
        var died = knownSymbols.Where(s => !allNames.Contains(s)).ToList();

        var survivalRate = (double) survived.Count / knownSymbols.Count;

        const double MinRequiredSurvival = 0.80;
        Assert.True(survivalRate >= MinRequiredSurvival,
                    $"Calibration: only {survived.Count}/{knownSymbols.Count} known symbols survived "
                  + $"({survivalRate:P0}); need >= {MinRequiredSurvival:P0}. Died: {string.Join(", ", died)}");
    }

    private static LibraryProfile MakeAeroScriptProfile() =>
        new()
            {
                Id = "aerotech-aeroscript/2025.3",
                LibraryId = "aerotech-aeroscript",
                Version = "2025.3",
                Source = "test-fixture",
                Languages = ["AeroScript"],
                Casing = new CasingConventions
                             {
                                 Types = "PascalCase",
                                 Methods = "PascalCase",
                                 Members = "PascalCase",
                                 Parameters = "PascalCase"
                             },
                Separators = ["."],
                CallableShapes = ["Foo()"],
                LikelySymbols = ["MoveLinear", "AxisStatus", "HomeType", "AxisFault", "RampMode"]
            };

    private static IReadOnlyList<string> LoadChunks()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "AeroScriptChunks.json");
        var json = File.ReadAllText(path);
        var doc = JsonSerializer.Deserialize<ChunkFixture>(json, smJsonOptions);
        Assert.NotNull(doc);
        return doc.Chunks;
    }

    private static IReadOnlyList<string> LoadKnownSymbols()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "AeroScriptKnownSymbols.json");
        var json = File.ReadAllText(path);
        var doc = JsonSerializer.Deserialize<KnownSymbolsFixture>(json, smJsonOptions);
        Assert.NotNull(doc);
        return doc.Symbols;
    }

    private static void AssertContains(HashSet<string> names, string expected) =>
        Assert.True(names.Contains(expected),
                    $"Expected '{expected}' in extracted type names. Got: {string.Join(", ", names.OrderBy(n => n, StringComparer.Ordinal))}");

    private static void AssertAbsent(HashSet<string> names, string forbidden) =>
        Assert.False(names.Contains(forbidden),
                     $"Junk word '{forbidden}' should not have survived extraction");

    private record ChunkFixture(IReadOnlyList<string> Chunks);

    private record KnownSymbolsFixture(IReadOnlyList<string> Symbols);

    private static readonly JsonSerializerOptions smJsonOptions = new()
                                                                      {
                                                                          PropertyNameCaseInsensitive = true
                                                                      };
}
