// // CliReconFallback.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using System.Text;
using System.Text.Json;
using DocRAG.Core.Models;
using DocRAG.Ingestion.Embedding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;

#endregion

namespace DocRAG.Ingestion.Recon;

/// <summary>
///     Local-Ollama-driven recon for environments without a calling LLM in
///     the loop (typically DocRAG.Cli invocations from a shell). The MCP
///     path delegates recon to the calling LLM (Claude / GPT / etc.) and
///     never uses this; this class exists so the CLI can still ingest
///     libraries when used standalone.
///
///     Confidence-gated: the caller (the CLI command) checks the result's
///     Confidence against OllamaSettings.ReconMinConfidence and refuses to
///     persist below the threshold without an explicit opt-in flag.
/// </summary>
public class CliReconFallback
{
    public CliReconFallback(IOptions<OllamaSettings> settings,
                            ILogger<CliReconFallback> logger)
    {
        mSettings = settings.Value;
        mLogger = logger;
        mClient = new OllamaApiClient(new Uri(mSettings.Endpoint));
    }

    private readonly OllamaApiClient mClient;
    private readonly OllamaSettings mSettings;
    private readonly ILogger<CliReconFallback> mLogger;

    /// <summary>
    ///     Verify the configured ReconModel is pulled and available locally.
    ///     CLI uses this before running recon to refuse-fast (rather than
    ///     silently falling back to a smaller model).
    /// </summary>
    public async Task<bool> IsModelAvailableAsync(CancellationToken ct = default)
    {
        bool available = false;
        try
        {
            var models = await mClient.ListLocalModelsAsync(ct);
            available = models.Any(m => string.Equals(m.Name, mSettings.ReconModel, StringComparison.OrdinalIgnoreCase));
        }
        catch(Exception ex)
        {
            mLogger.LogWarning(ex, "Failed to query Ollama for local models");
        }

        return available;
    }

    /// <summary>
    ///     Run a low-cost recon pass for (libraryId, version, url). Returns a
    ///     LibraryProfile with the model's self-reported confidence. The
    ///     profile is NOT persisted here — the CLI command persists it
    ///     after checking confidence.
    /// </summary>
    public async Task<LibraryProfile> ReconAsync(string url,
                                                 string libraryId,
                                                 string version,
                                                 CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var prompt = BuildPrompt(url, libraryId, version);
        var responseText = await CallOllamaAsync(prompt, ct);
        var parsed = ParseResponse(responseText);
        var profile = LibraryProfileService.Build(libraryId,
                                                  version,
                                                  parsed.Languages,
                                                  parsed.Casing,
                                                  parsed.Separators,
                                                  parsed.CallableShapes,
                                                  parsed.LikelySymbols,
                                                  parsed.CanonicalInventoryUrl,
                                                  parsed.Confidence,
                                                  SourceCliOllama
                                                 );
        return profile;
    }

    private async Task<string> CallOllamaAsync(string prompt, CancellationToken ct)
    {
        var request = new GenerateRequest
                          {
                              Model = mSettings.ReconModel,
                              Prompt = prompt,
                              Stream = true
                          };

        var responseBuilder = new StringBuilder();
        await foreach(var token in mClient.GenerateAsync(request, ct))
        {
            if (responseBuilder.Length < MaxResponseChars)
                responseBuilder.Append(token?.Response ?? string.Empty);
        }

        return responseBuilder.ToString().Trim();
    }

    private static string BuildPrompt(string url, string libraryId, string version)
    {
        var jsonExample = """
                          {
                            "languages": ["..."],
                            "casing": {
                              "types": "PascalCase|camelCase|snake_case|...",
                              "methods": "...",
                              "constants": "...",
                              "members": "...",
                              "parameters": "..."
                            },
                            "separators": [".", "::"],
                            "callableShapes": ["Foo()", "Foo<T>()"],
                            "likelySymbols": ["..."],
                            "canonicalInventoryUrl": null,
                            "confidence": 0.0
                          }
                          """;

        var result = $$"""
                       You are characterizing a documentation site so a downstream parser knows
                       how to extract identifier symbols. Given the library name, version, and
                       URL, infer language(s) and naming conventions and produce a profile.

                       Output ONLY a JSON object matching this schema:
                       {{jsonExample}}

                       - languages: programming languages this library's docs cover (e.g. "C#", "Python", "AeroScript").
                       - casing: per-category naming convention strings (PascalCase / camelCase / snake_case / SCREAMING_SNAKE / kebab-case / mixed).
                       - separators: token-joining symbols used in qualified names. ".", "::", "->", ":" — pick what applies.
                       - callableShapes: shapes that indicate "this is a function call". Common: "Foo()", "Foo<T>()".
                       - likelySymbols: 5-30 plausible top-level type/function names you'd expect to find. Best guess; the parser will verify against the corpus.
                       - canonicalInventoryUrl: if you know an enum-index or type-index page exists, its URL. Otherwise null.
                       - confidence: 0..1 self-rating of how sure you are about the above.

                       Library: {{libraryId}}
                       Version: {{version}}
                       URL: {{url}}

                       Respond with the JSON object only.
                       """;
        return result;
    }

    private static ReconParseResult ParseResponse(string responseText)
    {
        var cleaned = responseText
                      .Replace(JsonCodeFenceOpen, string.Empty, StringComparison.Ordinal)
                      .Replace(CodeFence, string.Empty, StringComparison.Ordinal)
                      .Trim();

        var languages = Array.Empty<string>();
        var casing = new CasingConventions();
        var separators = Array.Empty<string>();
        var callables = Array.Empty<string>();
        var likely = Array.Empty<string>();
        string? inventoryUrl = null;
        var confidence = 0f;

        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;

            languages = ReadStringArray(root, KeyLanguages);
            casing = ReadCasing(root);
            separators = ReadStringArray(root, KeySeparators);
            callables = ReadStringArray(root, KeyCallableShapes);
            likely = ReadStringArray(root, KeyLikelySymbols);
            inventoryUrl = ReadOptionalString(root, KeyCanonicalInventoryUrl);
            confidence = ReadConfidence(root);
        }
        catch(JsonException)
        {
            // Parsing failed — return zero-confidence empty profile. Caller's
            // confidence gate will reject this, refusing to persist.
        }

        var result = new ReconParseResult(languages, casing, separators, callables, likely, inventoryUrl, confidence);
        return result;
    }

    private static string[] ReadStringArray(JsonElement root, string key)
    {
        string[] result = [];
        if (root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Array)
            result = prop.EnumerateArray()
                         .Select(v => v.GetString() ?? string.Empty)
                         .Where(s => !string.IsNullOrEmpty(s))
                         .ToArray();
        return result;
    }

    private static CasingConventions ReadCasing(JsonElement root)
    {
        var result = new CasingConventions();
        if (root.TryGetProperty(KeyCasing, out var casingProp) && casingProp.ValueKind == JsonValueKind.Object)
            result = new CasingConventions
                         {
                             Types = ReadOptionalString(casingProp, KeyTypes) ?? string.Empty,
                             Methods = ReadOptionalString(casingProp, KeyMethods) ?? string.Empty,
                             Constants = ReadOptionalString(casingProp, KeyConstants) ?? string.Empty,
                             Members = ReadOptionalString(casingProp, KeyMembers) ?? string.Empty,
                             Parameters = ReadOptionalString(casingProp, KeyParameters) ?? string.Empty
                         };
        return result;
    }

    private static string? ReadOptionalString(JsonElement root, string key)
    {
        string? result = null;
        if (root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
            result = prop.GetString();
        return result;
    }

    private static float ReadConfidence(JsonElement root)
    {
        float result = 0f;
        if (root.TryGetProperty(KeyConfidence, out var prop) && prop.ValueKind == JsonValueKind.Number)
            result = (float) prop.GetDouble();
        return Math.Clamp(result, 0f, 1f);
    }

    private record ReconParseResult(IReadOnlyList<string> Languages,
                                    CasingConventions Casing,
                                    IReadOnlyList<string> Separators,
                                    IReadOnlyList<string> CallableShapes,
                                    IReadOnlyList<string> LikelySymbols,
                                    string? CanonicalInventoryUrl,
                                    float Confidence);

    private const int MaxResponseChars = 8192;
    private const string SourceCliOllama = "cli-ollama";
    private const string JsonCodeFenceOpen = "```json";
    private const string CodeFence = "```";
    private const string KeyLanguages = "languages";
    private const string KeyCasing = "casing";
    private const string KeyTypes = "types";
    private const string KeyMethods = "methods";
    private const string KeyConstants = "constants";
    private const string KeyMembers = "members";
    private const string KeyParameters = "parameters";
    private const string KeySeparators = "separators";
    private const string KeyCallableShapes = "callableShapes";
    private const string KeyLikelySymbols = "likelySymbols";
    private const string KeyCanonicalInventoryUrl = "canonicalInventoryUrl";
    private const string KeyConfidence = "confidence";
}
