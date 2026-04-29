// LlmClassifier.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.Text;
using System.Text.Json;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Embedding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;

#endregion

namespace SaddleRAG.Ingestion.Classification;

/// <summary>
///     LLM-based page classifier using Ollama chat completion.
///     Authoritative classification â€” overrides heuristic results.
/// </summary>
public class LlmClassifier
{
    public LlmClassifier(IOptions<OllamaSettings> settings,
                         ILogger<LlmClassifier> logger)
    {
        mSettings = settings.Value;
        mLogger = logger;
        mClient = new OllamaApiClient(new Uri(mSettings.Endpoint));
    }

    private readonly OllamaApiClient mClient;
    private readonly ILogger<LlmClassifier> mLogger;
    private readonly OllamaSettings mSettings;

    /// <summary>
    ///     Version string used by LibraryManifest.LastClassifierVersion to
    ///     detect when reclassification is needed during rescrub. Combines
    ///     the configured classification model with a manually-bumped prompt
    ///     version. Bump <see cref="PromptVersion" /> whenever the prompt
    ///     template changes meaningfully.
    /// </summary>
    public const string PromptVersion = "v1";

    /// <summary>
    ///     Returns the current classifier version for this instance, used
    ///     by RescrubService to populate the manifest and decide whether
    ///     reclassification is needed on a future rescrub.
    /// </summary>
    public string GetCurrentVersion() => $"{mSettings.ClassificationModel}-{PromptVersion}";

    /// <summary>
    ///     Classify a page using the LLM. Returns category and confidence.
    /// </summary>
    public async Task<(DocCategory Category, float Confidence)> ClassifyAsync(PageRecord page,
                                                                              string libraryHint,
                                                                              CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrEmpty(libraryHint);

        var contentPreview = page.RawContent.Length > MaxPreviewChars
                                 ? page.RawContent[..MaxPreviewChars]
                                 : page.RawContent;

        var jsonExample = """{"category": "...", "confidence": 0.0-1.0}""";

        var prompt = $"""
                      You are a documentation classifier. Given a page's metadata and content preview,
                      classify it into exactly one category. Respond with ONLY a JSON object:
                      {jsonExample}

                      Categories:
                      - Overview: Conceptual explanation, architecture, "about" pages
                      - HowTo: Step-by-step guide, tutorial, walkthrough
                      - Sample: Code samples, demos, example projects showing how to use the library
                      - Code: Library source code, implementation files (not usage examples)
                      - ApiReference: API docs â€” class, method, property, event reference
                      - ChangeLog: Release notes, migration guides, what's new
                      - Unclassified: Does not fit other categories

                      Library: {libraryHint}
                      URL: {page.Url}
                      Title: {page.Title}

                      Content preview:
                      {contentPreview}
                      """;

        var category = DocCategory.Unclassified;
        var confidence = 0f;

        try
        {
            var request = new GenerateRequest
                              {
                                  Model = mSettings.ClassificationModel,
                                  Prompt = prompt,
                                  Stream = true
                              };

            var responseBuilder = new StringBuilder();
            await foreach(var token in mClient.GenerateAsync(request, ct))
            {
                if (responseBuilder.Length < MaxResponseChars)
                    responseBuilder.Append(token?.Response ?? string.Empty);
            }

            var parsed = ParseClassificationResponse(responseBuilder.ToString().Trim());
            category = parsed.Category;
            confidence = parsed.Confidence;

            mLogger.LogDebug("LLM classified {Url} as {Category} (confidence: {Confidence:F2})",
                             page.Url,
                             category,
                             confidence
                            );
        }
        catch(Exception ex)
        {
            mLogger.LogWarning(ex, "LLM classification failed for {Url}", page.Url);
        }

        return (category, confidence);
    }

    private static (DocCategory Category, float Confidence) ParseClassificationResponse(string responseText)
    {
        var cleaned = responseText
                      .Replace(JsonCodeFenceOpen, string.Empty)
                      .Replace(CodeFence, string.Empty)
                      .Trim();

        var category = DocCategory.Unclassified;
        var confidence = 0f;

        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;

            if (root.TryGetProperty(CategoryKey, out var catProp))
            {
                var catString = catProp.GetString() ?? string.Empty;
                Enum.TryParse(catString, ignoreCase: true, out category);
            }

            if (root.TryGetProperty(ConfidenceKey, out var confProp))
            {
                confidence = confProp.ValueKind switch
                    {
                        JsonValueKind.Number => (float) confProp.GetDouble(),
                        var _ => 0f
                    };
            }
        }
        catch(JsonException)
        {
            foreach(var cat in Enum.GetValues<DocCategory>())
            {
                if (cleaned.Contains(cat.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    category = cat;
                    confidence = 0.5f;
                    break;
                }
            }
        }

        return (category, confidence);
    }

    private const int MaxPreviewChars = 500;
    private const int MaxResponseChars = 4096;
    private const string JsonCodeFenceOpen = "```json";
    private const string CodeFence = "```";
    private const string CategoryKey = "category";
    private const string ConfidenceKey = "confidence";
}
