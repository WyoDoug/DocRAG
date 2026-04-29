// CrossEncoderReRanker.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;

#endregion

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>
///     Cross-encoder-style reranker. Hosts the Mixedbread
///     mxbai-rerank-large-v2 model on Ollama as a generate model with a
///     "rate this 0.0-1.0" prompt, scoring each (query, document) pair
///     independently. Produces continuous floats instead of the legacy
///     OllamaReRanker's 5-bucket plateau, and the per-pair scoring
///     pattern matches how cross-encoders are actually trained.
///
///     Latency: roughly 50-200ms per (query, document) pair. For a
///     typical maxResults=5 with candidateMultiplier=2 (10 candidates)
///     that's ~0.5-2s total — slower than vector-only or hybrid alone,
///     but an order of magnitude better than the legacy reranker's
///     2-7s batch call AND with continuous scoring.
/// </summary>
public class CrossEncoderReRanker : IReRanker
{
    public CrossEncoderReRanker(IOptions<OllamaSettings> settings,
                                ILogger<CrossEncoderReRanker> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        mSettings = settings.Value;
        mLogger = logger;
        mClient = new OllamaApiClient(new Uri(mSettings.Endpoint));
    }

    private readonly OllamaApiClient mClient;
    private readonly ILogger<CrossEncoderReRanker> mLogger;
    private readonly OllamaSettings mSettings;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReRankResult>> ReRankAsync(string query,
                                                               IReadOnlyList<DocChunk> candidates,
                                                               int maxResults,
                                                               CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);
        ArgumentNullException.ThrowIfNull(candidates);

        var scored = new List<ReRankResult>(candidates.Count);
        foreach(var chunk in candidates)
        {
            var score = await ScoreOneAsync(query, chunk.Content, ct);
            scored.Add(new ReRankResult
                           {
                               Chunk = chunk,
                               RelevanceScore = score
                           }
                      );
        }

        var ordered = scored.OrderByDescending(r => r.RelevanceScore).Take(maxResults).ToList();
        return ordered;
    }

    private async Task<float> ScoreOneAsync(string query, string document, CancellationToken ct)
    {
        var prompt = BuildPrompt(query, TruncateDocument(document));
        var responseText = await CallOllamaAsync(prompt, ct);
        var score = ParseScore(responseText);
        return score;
    }

    private async Task<string> CallOllamaAsync(string prompt, CancellationToken ct)
    {
        var request = new GenerateRequest
                          {
                              Model = mSettings.CrossEncoderModel,
                              Prompt = prompt,
                              Stream = true
                          };

        var responseBuilder = new StringBuilder();
        try
        {
            await foreach(var token in mClient.GenerateAsync(request, ct))
            {
                if (responseBuilder.Length < MaxResponseChars)
                    responseBuilder.Append(token?.Response ?? string.Empty);
            }
        }
        catch(Exception ex) when(ex is not OperationCanceledException)
        {
            mLogger.LogWarning(ex, "Cross-encoder rerank call failed; treating as score 0");
        }

        return responseBuilder.ToString().Trim();
    }

    private static string TruncateDocument(string content)
    {
        var result = content.Length <= MaxDocumentChars ? content : content[..MaxDocumentChars];
        return result;
    }

    private static string BuildPrompt(string query, string document)
    {
        var prompt = $$"""
                       You are an expert relevance judge. Rate how relevant the document is to the query
                       on a scale from 0.0 (completely irrelevant) to 1.0 (perfect match).
                       Respond with ONLY the number, nothing else.

                       Query: {{query}}
                       Document: {{document}}
                       Relevance score:
                       """;
        return prompt;
    }

    /// <summary>
    ///     Tolerant parser. The model is asked to emit only a number but
    ///     occasionally adds prefixes ("Relevance: 0.85") or extra text
    ///     ("0.97 (high confidence)"). Extracts the first float-like
    ///     token in [0, 1] from the response, falling back to 0 if no
    ///     float is found.
    /// </summary>
    public static float ParseScore(string responseText)
    {
        ArgumentNullException.ThrowIfNull(responseText);

        var score = 0f;
        var match = smFloatRegex.Match(responseText);
        if (match.Success && float.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw))
            score = Math.Clamp(raw, MinScore, MaxScore);

        return score;
    }

    // Matches floats like "0.85", "0.0", ".97", "1", "1.0".
    // Captures the first plausible relevance score the model returns.
    private static readonly Regex smFloatRegex = new(
        @"\d*\.\d+|\d+(?:\.\d+)?",
        RegexOptions.Compiled
    );

    private const int MaxResponseChars = 256;
    private const int MaxDocumentChars = 2000;
    private const float MinScore = 0f;
    private const float MaxScore = 1f;
}
