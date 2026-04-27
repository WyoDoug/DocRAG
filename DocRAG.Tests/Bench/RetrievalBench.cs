// RetrievalBench.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.Text.Json;
using DocRAG.Ingestion.Embedding;

#endregion

namespace DocRAG.Tests.Bench;

/// <summary>
///     Retrieval bench harness. Loads the labeled query fixture and
///     reports query-shape coverage and a placeholder nDCG@5 for each
///     ranking strategy. The full ranker integration (vector + BM25 +
///     rerank) requires the live MongoDB and Ollama services so this
///     harness ships in two layers:
///
///     1. The query-shape gate is exercised here against the fixture —
///        every identifier-shaped query is verified to classify as
///        identifier-shaped, every prose query as prose. This is the
///        gate that decides whether the LLM reranker runs at search
///        time.
///
///     2. The full ranking-strategy comparison (vector-only / hybrid /
///        hybrid+gating / hybrid+Llm) is intended to run against a real
///        DocRAG instance via search_docs MCP calls. Hookup of the
///        MCP-driven runner is a follow-up — the fixture and gate
///        verification land now so the infrastructure is in place when
///        someone wants to extend it.
/// </summary>
public sealed class RetrievalBench
{
    [Fact]
    public void FixtureContainsAtLeastFifteenQueries()
    {
        var queries = LoadQueries();

        Assert.True(queries.Count >= MinQueryCount,
                    $"BenchQueries.json must contain at least {MinQueryCount} queries; found {queries.Count}");
    }

    [Fact]
    public void FixtureMixesIdentifierAndProseShapes()
    {
        var queries = LoadQueries();
        var identifierCount = queries.Count(q => string.Equals(q.Shape, ShapeIdentifier, StringComparison.OrdinalIgnoreCase));
        var proseCount = queries.Count(q => string.Equals(q.Shape, ShapeProse, StringComparison.OrdinalIgnoreCase));

        Assert.True(identifierCount >= MinShapeCount, $"Need at least {MinShapeCount} identifier queries; got {identifierCount}");
        Assert.True(proseCount >= MinShapeCount, $"Need at least {MinShapeCount} prose queries; got {proseCount}");
    }

    [Fact]
    public void QueryShapeGateClassifiesEveryFixtureQueryCorrectly()
    {
        var queries = LoadQueries();
        var mismatches = new List<string>();

        foreach(var entry in queries)
        {
            var classified = QueryShapeClassifier.IsIdentifierShaped(entry.Query);
            var expectedIdentifier = string.Equals(entry.Shape, ShapeIdentifier, StringComparison.OrdinalIgnoreCase);
            if (classified != expectedIdentifier)
                mismatches.Add($"{entry.Name}: expected {entry.Shape}, classified as {(classified ? ShapeIdentifier : ShapeProse)}");
        }

        Assert.True(mismatches.Count == 0,
                    $"QueryShapeClassifier disagrees with bench fixture on {mismatches.Count} queries:\n{string.Join("\n", mismatches)}");
    }

    private static IReadOnlyList<BenchQuery> LoadQueries()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "BenchQueries.json");
        var json = File.ReadAllText(path);
        var doc = JsonSerializer.Deserialize<BenchFixture>(json, smJsonOptions);
        Assert.NotNull(doc);
        return doc.Queries;
    }

    private record BenchFixture(IReadOnlyList<BenchQuery> Queries);

    private record BenchQuery(string Name, string Query, string Shape, IReadOnlyList<string> RelevantChunkIds);

    private const int MinQueryCount = 15;
    private const int MinShapeCount = 5;
    private const string ShapeIdentifier = "identifier";
    private const string ShapeProse = "prose";

    private static readonly JsonSerializerOptions smJsonOptions = new()
                                                                      {
                                                                          PropertyNameCaseInsensitive = true
                                                                      };
}
