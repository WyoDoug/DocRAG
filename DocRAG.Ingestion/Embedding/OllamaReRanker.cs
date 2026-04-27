// OllamaReRanker.cs

// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.



#region Usings



using System.Text;

using DocRAG.Core.Interfaces;

using DocRAG.Core.Models;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Options;

using OllamaSharp;

using OllamaSharp.Models;



#endregion



namespace DocRAG.Ingestion.Embedding;



/// <summary>

///     Ollama-based batch re-ranker.

///     Sends all candidates in a single prompt and parses one score per document.

///     Uses a categorical word-list (PERFECT/HIGH/MEDIUM/LOW/NONE) for reliable parsing.

/// </summary>

public class OllamaReRanker : IReRanker

{

    public OllamaReRanker(IOptions<OllamaSettings> settings,

                          ILogger<OllamaReRanker> logger)

    {

        mSettings = settings.Value;

        mLogger = logger;

        mClient = new OllamaApiClient(new Uri(mSettings.Endpoint));

    }



    private readonly OllamaApiClient mClient;

    private readonly ILogger<OllamaReRanker> mLogger;

    private readonly OllamaSettings mSettings;



    /// <inheritdoc />

    public async Task<IReadOnlyList<ReRankResult>> ReRankAsync(string query,

                                                               IReadOnlyList<DocChunk> candidates,

                                                               int maxResults,

                                                               CancellationToken ct = default)

    {

        ArgumentException.ThrowIfNullOrEmpty(query);

        ArgumentNullException.ThrowIfNull(candidates);



        var scores = await ScoreBatchAsync(query, candidates, ct);



        var results = candidates

                      .Select((chunk, index) => new ReRankResult

                                                    {

                                                        Chunk = chunk,

                                                        RelevanceScore = index < scores.Count ? scores[index] : 0f

                                                    }

                             )

                      .OrderByDescending(r => r.RelevanceScore)

                      .Take(maxResults)

                      .ToList();



        return results;

    }



    private async Task<IReadOnlyList<float>> ScoreBatchAsync(string query,

                                                             IReadOnlyList<DocChunk> candidates,

                                                             CancellationToken ct)

    {

        var promptBuilder = new StringBuilder();

        promptBuilder.AppendLine(NoThinkDirective);

        promptBuilder.AppendLine(RelevanceRatingInstruction);

        promptBuilder

            .AppendLine(CategoryWordListInstruction);

        promptBuilder.AppendLine();

        promptBuilder.AppendLine($"Query: {query}");

        promptBuilder.AppendLine();



        for(var i = 0; i < candidates.Count; i++)

        {

            string content = candidates[i].Content;

            string truncated = content.Length > MaxContentCharsPerDoc

                                   ? content[..MaxContentCharsPerDoc]

                                   : content;



            promptBuilder.AppendLine($"--- Document {i + 1}: {candidates[i].PageTitle} ---");

            promptBuilder.AppendLine(truncated);

            promptBuilder.AppendLine();

        }



        promptBuilder.AppendLine(ReplyFormatInstruction);

        promptBuilder.AppendLine(ReplyFormatExampleMedium);

        promptBuilder.AppendLine(ReplyFormatExampleHigh);

        promptBuilder.AppendLine(ReplyFormatExampleEtc);



        var scores = new List<float>();



        try

        {

            var request = new GenerateRequest

                              {

                                  Model = mSettings.ReRankingModel,

                                  Prompt = promptBuilder.ToString(),

                                  Stream = true,

                                  Options = new RequestOptions

                                                {

                                                    Temperature = 0f

                                                }

                              };



            var responseBuilder = new StringBuilder();

            await foreach(var token in mClient.GenerateAsync(request, ct))

            {

                if (responseBuilder.Length < MaxResponseChars)

                    responseBuilder.Append(token?.Response ?? string.Empty);

            }



            string responseText = responseBuilder.ToString().Trim();

            scores = ParseBatchScores(responseText, candidates.Count);



            mLogger.LogDebug("Batch re-rank response for {Count} candidates: {Response}",

                             candidates.Count,

                             responseText

                            );

        }

        catch(Exception ex)

        {

            mLogger.LogWarning(ex, "Batch re-ranking failed for {Count} candidates", candidates.Count);

        }



        // Pad with zeros if parsing returned fewer scores than candidates

        while (scores.Count < candidates.Count)

            scores.Add(item: 0f);



        return scores;

    }



    private List<float> ParseBatchScores(string responseText, int expectedCount)

    {

        var scores = new List<float>(expectedCount);

        string[] lines = responseText.Split(separator: '\n', StringSplitOptions.RemoveEmptyEntries);



        foreach(string line in lines)

        {

            if (scores.Count >= expectedCount)

                break;



            var score = 0f;

            string[] words = line.Split(' ', ':', '\t', '.', ',');



            var found = false;

            foreach(string word in words)

            {

                if (!found && smScoreMap.TryGetValue(word.Trim(), out float mapped))

                {

                    score = mapped;

                    found = true;

                }

            }



            if (found)

                scores.Add(score);

        }



        return scores;

    }



    private const int MaxContentCharsPerDoc = 500;

    private const int MaxResponseChars = 2048;



    private const string NoThinkDirective = "/no_think";
    private const string RelevanceRatingInstruction = "Rate how relevant each document is to the search query.";
    private const string CategoryWordListInstruction = "For each document, reply with its number and ONE word: PERFECT, HIGH, MEDIUM, LOW, or NONE.";
    private const string ReplyFormatInstruction = "Reply with ONLY one line per document in this exact format:";
    private const string ReplyFormatExampleMedium = "1: MEDIUM";
    private const string ReplyFormatExampleHigh = "2: HIGH";
    private const string ReplyFormatExampleEtc = "etc.";

    private static readonly Dictionary<string, float> smScoreMap =

        new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)

            {

                ["PERFECT"] = 1.0f,

                ["HIGH"] = 0.8f,

                ["MEDIUM"] = 0.5f,

                ["LOW"] = 0.2f,

                ["NONE"] = 0.0f

            };

}

