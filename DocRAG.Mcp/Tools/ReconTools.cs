// ReconTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using DocRAG.Core.Models;
using DocRAG.Database.Repositories;
using DocRAG.Ingestion.Recon;
using ModelContextProtocol.Server;

#endregion

namespace DocRAG.Mcp.Tools;

/// <summary>
///     MCP tools for the pre-scrape reconnaissance flow. recon_library is
///     pure metadata — it returns instructions and a JSON schema and asks
///     the calling LLM to do the actual reconnaissance (browse the docs
///     site, characterize languages and conventions, list likely symbols)
///     and report back via submit_library_profile. The MCP server itself
///     does no LLM work in this path — the calling LLM is frontier-grade
///     and far better suited to the task than the local Ollama models.
/// </summary>
[McpServerToolType]
public static class ReconTools
{
    [McpServerTool(Name = "recon_library")]
    [Description("Get the instructions and JSON schema needed to characterize a docs " +
                 "site before scraping. The calling LLM should browse the URL, identify " +
                 "the documentation language(s), naming/casing conventions, token " +
                 "separators, callable shapes, and likely top-level symbols, then call " +
                 "submit_library_profile with the resulting JSON. Returns immediately " +
                 "with no LLM work on the server side."
                )]
    public static string ReconLibrary([Description("Root URL of the docs site to characterize")]
                                      string url,
                                      [Description("Library identifier the profile will apply to")]
                                      string library,
                                      [Description("Library version the profile will apply to")]
                                      string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var payload = new
                          {
                              Url = url,
                              Library = library,
                              Version = version,
                              Instructions = ReconInstructions,
                              JsonSchema = JsonExample,
                              Hints = smReconHints,
                              SamplePagesToInspect = smSamplePages,
                              CallbackTool = SubmitProfileToolName
                          };

        var json = JsonSerializer.Serialize(payload, smJsonOptions);
        return json;
    }

    [McpServerTool(Name = "submit_library_profile")]
    [Description("Submit the reconnaissance JSON produced by recon_library. The server " +
                 "validates the payload and persists it as the LibraryProfile for " +
                 "(library, version). Idempotent — replaces any existing profile. " +
                 "After this completes, call start_ingest again to advance the state " +
                 "machine."
                )]
    public static async Task<string> SubmitLibraryProfile(LibraryProfileService service,
                                                          RepositoryFactory repositoryFactory,
                                                          [Description("Library identifier the profile applies to")]
                                                          string library,
                                                          [Description("Library version the profile applies to")]
                                                          string version,
                                                          [Description("JSON payload matching the schema returned by recon_library")]
                                                          string profileJson,
                                                          [Description("Optional database profile name")]
                                                          string? profile = null,
                                                          CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(profileJson);

        var parsed = ParseProfileJson(profileJson, library, version);
        var profileRepo = repositoryFactory.GetLibraryProfileRepository(profile);
        var saved = await service.SaveAsync(profileRepo, parsed, ct);

        var response = new
                           {
                               saved.Id,
                               saved.LibraryId,
                               saved.Version,
                               saved.Confidence,
                               saved.Source,
                               saved.SchemaVersion,
                               LikelySymbolCount = saved.LikelySymbols.Count,
                               saved.CanonicalInventoryUrl,
                               Message = ProfileSavedMessage
                           };

        var json = JsonSerializer.Serialize(response, smJsonOptions);
        return json;
    }

    private static LibraryProfile ParseProfileJson(string profileJson, string libraryId, string version)
    {
        using var doc = JsonDocument.Parse(profileJson);
        var root = doc.RootElement;

        var languages = ReadStringArray(root, KeyLanguages);
        var casing = ReadCasing(root);
        var separators = ReadStringArray(root, KeySeparators);
        var callables = ReadStringArray(root, KeyCallableShapes);
        var likely = ReadStringArray(root, KeyLikelySymbols);
        var inventoryUrl = ReadOptionalString(root, KeyCanonicalInventoryUrl);
        var confidence = ReadConfidence(root);
        var source = ReadOptionalString(root, KeySource) ?? SourceCallingLlm;

        var result = LibraryProfileService.Build(libraryId,
                                                 version,
                                                 languages,
                                                 casing,
                                                 separators,
                                                 callables,
                                                 likely,
                                                 inventoryUrl,
                                                 confidence,
                                                 source
                                                );
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
        float result = DefaultSubmittedConfidence;
        if (root.TryGetProperty(KeyConfidence, out var prop) && prop.ValueKind == JsonValueKind.Number)
            result = (float) prop.GetDouble();
        return Math.Clamp(result, 0f, 1f);
    }

    private static readonly JsonSerializerOptions smJsonOptions = new() { WriteIndented = true };

    private const string ProfileSavedMessage =
        "Profile saved. Call start_ingest again to advance — it should now report READY_TO_SCRAPE or READY.";

    private const string SourceCallingLlm = "calling-llm";

    private const float DefaultSubmittedConfidence = 0.85f;

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
    private const string KeySource = "source";

    private const string SubmitProfileToolName = "submit_library_profile";

    private const string ReconInstructions = """
        Browse the URL and (when useful) one or two sample pages to characterize this docs site.
        Identify the documentation language(s), the casing conventions used for types / methods /
        constants / members / parameters, the token separators that appear in qualified names
        (".", "::", "->", ":"), recognized callable shapes ("Foo()", "Foo<T>()"), and 5-30
        plausible top-level type / function / parameter names. The likelySymbols list is a soft
        hint — if you miss real symbols, the corpus-context rules will recover them, so optimize
        for precision (avoid junk like "Each" or "When" picked from prose) over recall.
        Self-rate your confidence in [0,1]. Return the JSON object as input to submit_library_profile.
        """;

    private const string JsonExample = """
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
                                         "confidence": 0.0,
                                         "source": "calling-llm"
                                       }
                                       """;

    private static readonly string[] smReconHints =
    {
        "Look for an enum index page (e.g. *-Enums.htm) — set canonicalInventoryUrl if found.",
        "If the docs cover multiple languages, list them all in languages[] in order of prominence.",
        "Aerotech-style docs use \".\" separators in qualified names like AxisFault.Disabled.",
        "Python docs are PascalCase types and snake_case functions — note that mismatch.",
        "Skip prose words (Each, When, Represents, Values, For, Use) — they are not symbols."
    };

    private static readonly string[] smSamplePages =
    {
        "The root URL itself",
        "Any \"types\" / \"enums\" / \"reference\" landing page linked from root",
        "One sample API reference page so you see what a typical symbol shape looks like"
    };
}
