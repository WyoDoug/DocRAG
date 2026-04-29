// SymbolManagementTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using ModelContextProtocol.Server;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tools that let a calling LLM review and refine the symbol
///     extractor's per-library decisions:
///       — list_excluded_symbols: see what was rejected, with sample
///         sentences pulled from the corpus.
///       — add_to_likely_symbols: promote a token (extractor will keep it
///         even when it lacks structural signal).
///       — add_to_stoplist: demote a token (extractor will reject it
///         regardless of signal).
///
///     All three are optional. The rescrub_library tool's Hints field
///     suggests using them when the rejection count looks suspicious.
/// </summary>
[McpServerToolType]
public static class SymbolManagementTools
{
    /// <summary>
    ///     Return the per-(library, version) tokens that the symbol extractor
    ///     rejected during the last rescrub, with the reason and a few sample
    ///     sentences. Use to triage which rejections are correct (noise) and
    ///     which to override via add_to_likely_symbols.
    /// </summary>
    [McpServerTool(Name = "list_excluded_symbols")]
    [Description("Return the per-(library, version) tokens that the symbol extractor " +
                 "rejected during the last rescrub, with the reason and a few sample " +
                 "sentences. Use to triage which rejections are correct (noise) and " +
                 "which to override via add_to_likely_symbols.")]
    public static async Task<string> ListExcludedSymbols(RepositoryFactory repositoryFactory,
                                                          [Description("Library identifier")]
                                                          string library,
                                                          [Description("Library version")]
                                                          string version,
                                                          [Description("Optional reason filter (GlobalStoplist, LibraryStoplist, Unit, BelowMinLength, LikelyAbbreviation, NoStructureSignal).")]
                                                          SymbolRejectionReason? reason = null,
                                                          [Description("Maximum entries to return. Default 50.")]
                                                          int limit = DefaultListLimit,
                                                          [Description("Optional database profile name")]
                                                          string? profile = null,
                                                          CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var profileRepo = repositoryFactory.GetLibraryProfileRepository(profile);
        var libraryProfile = await profileRepo.GetAsync(library, version, ct);

        string result;
        if (libraryProfile == null)
        {
            result = JsonSerializer.Serialize(new { ReconNeeded = true, Library = library, Version = version },
                                              smJsonOptions);
        }
        else
        {
            var excludedRepo = repositoryFactory.GetExcludedSymbolsRepository(profile);
            var items = await excludedRepo.ListAsync(library, version, reason, limit, ct);
            var total = await excludedRepo.CountAsync(library, version, ct);
            result = JsonSerializer.Serialize(new
                                                  {
                                                      Library = library,
                                                      Version = version,
                                                      TotalExcluded = total,
                                                      Returned = items.Count,
                                                      Items = items.Select(i => new
                                                                                    {
                                                                                        i.Name,
                                                                                        Reason = i.Reason.ToString(),
                                                                                        i.ChunkCount,
                                                                                        i.SampleSentences
                                                                                    })
                                                  }, smJsonOptions);
        }
        return result;
    }

    /// <summary>
    ///     Promote one or more tokens to LibraryProfile.LikelySymbols so the
    ///     extractor keeps them even without other structural signal. Auto-removes
    ///     any case-equivalent variant from LibraryProfile.Stoplist (last call wins).
    ///     Returns a summary of what changed plus a hint to call rescrub_library.
    /// </summary>
    [McpServerTool(Name = "add_to_likely_symbols")]
    [Description("Call list_excluded_symbols first to identify tokens the extractor rejected that should be kept. " +
                 "Promote one or more tokens to LibraryProfile.LikelySymbols so the " +
                 "extractor keeps them even without other structural signal. Auto-removes " +
                 "any case-equivalent variant from LibraryProfile.Stoplist (last call wins). " +
                 "Returns a summary of what changed plus a hint to call rescrub_library.")]
    public static async Task<string> AddToLikelySymbols(RepositoryFactory repositoryFactory,
                                                         [Description("Library identifier")]
                                                         string library,
                                                         [Description("Library version")]
                                                         string version,
                                                         [Description("Tokens to promote.")]
                                                         IReadOnlyList<string> names,
                                                         [Description("Optional database profile name")]
                                                         string? profile = null,
                                                         CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
            throw new ArgumentException("names must contain at least one entry", nameof(names));

        var profileRepo = repositoryFactory.GetLibraryProfileRepository(profile);
        var libraryProfile = await profileRepo.GetAsync(library, version, ct);

        string result;
        if (libraryProfile == null)
        {
            result = JsonSerializer.Serialize(new { ReconNeeded = true, Library = library, Version = version },
                                              smJsonOptions);
        }
        else
        {
            var nameSet = new HashSet<string>(names, StringComparer.Ordinal);
            var alreadyInLikely = libraryProfile.LikelySymbols.Where(nameSet.Contains).ToList();
            var promoted = nameSet.Where(n => !libraryProfile.LikelySymbols.Contains(n, StringComparer.Ordinal)).ToList();

            var newLikely = libraryProfile.LikelySymbols.Concat(promoted).ToList();
            var newStoplist = libraryProfile.Stoplist
                                            .Where(s => !nameSet.Contains(s, StringComparer.OrdinalIgnoreCase))
                                            .ToList();
            var removedFromStoplist = libraryProfile.Stoplist
                                                    .Where(s => nameSet.Contains(s, StringComparer.OrdinalIgnoreCase))
                                                    .ToList();

            var updated = libraryProfile with
                              {
                                  LikelySymbols = newLikely,
                                  Stoplist = newStoplist
                              };
            await profileRepo.UpsertAsync(updated, ct);

            var excludedRepo = repositoryFactory.GetExcludedSymbolsRepository(profile);
            await excludedRepo.RemoveAsync(library, version, names, ct);

            result = JsonSerializer.Serialize(new
                                                  {
                                                      Library = library,
                                                      Version = version,
                                                      Promoted = promoted,
                                                      AlreadyInLikelySymbols = alreadyInLikely,
                                                      RemovedFromStoplist = removedFromStoplist,
                                                      Hints = new[] { "Call rescrub_library to apply the changes." }
                                                  }, smJsonOptions);
        }
        return result;
    }

    /// <summary>
    ///     Demote one or more tokens to LibraryProfile.Stoplist so the extractor
    ///     rejects them regardless of structural signal. Case-insensitive — adding
    ///     'foo' blocks 'Foo', 'FOO', etc. Auto-removes case-equivalent entries
    ///     from LibraryProfile.LikelySymbols (last call wins). Returns a summary
    ///     of what changed plus a hint to call rescrub_library.
    /// </summary>
    [McpServerTool(Name = "add_to_stoplist")]
    [Description("Call list_excluded_symbols first to identify tokens the extractor accepted that should be rejected. " +
                 "Demote one or more tokens to LibraryProfile.Stoplist so the extractor " +
                 "rejects them regardless of structural signal. Case-insensitive — adding " +
                 "'foo' blocks 'Foo', 'FOO', etc. Auto-removes case-equivalent entries " +
                 "from LibraryProfile.LikelySymbols (last call wins). Returns a summary " +
                 "of what changed plus a hint to call rescrub_library.")]
    public static async Task<string> AddToStoplist(RepositoryFactory repositoryFactory,
                                                    [Description("Library identifier")]
                                                    string library,
                                                    [Description("Library version")]
                                                    string version,
                                                    [Description("Tokens to demote.")]
                                                    IReadOnlyList<string> names,
                                                    [Description("Optional database profile name")]
                                                    string? profile = null,
                                                    CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
            throw new ArgumentException("names must contain at least one entry", nameof(names));

        var profileRepo = repositoryFactory.GetLibraryProfileRepository(profile);
        var libraryProfile = await profileRepo.GetAsync(library, version, ct);

        string result;
        if (libraryProfile == null)
        {
            result = JsonSerializer.Serialize(new { ReconNeeded = true, Library = library, Version = version },
                                              smJsonOptions);
        }
        else
        {
            var nameSet = new HashSet<string>(names, StringComparer.Ordinal);
            var alreadyInStoplist = libraryProfile.Stoplist
                                                  .Where(s => nameSet.Contains(s, StringComparer.OrdinalIgnoreCase))
                                                  .ToList();
            var demoted = nameSet.Where(n => !libraryProfile.Stoplist.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();

            var newStoplist = libraryProfile.Stoplist.Concat(demoted).ToList();
            var newLikely = libraryProfile.LikelySymbols
                                          .Where(s => !nameSet.Contains(s, StringComparer.OrdinalIgnoreCase))
                                          .ToList();
            var removedFromLikely = libraryProfile.LikelySymbols
                                                  .Where(s => nameSet.Contains(s, StringComparer.OrdinalIgnoreCase))
                                                  .ToList();

            var updated = libraryProfile with
                              {
                                  Stoplist = newStoplist,
                                  LikelySymbols = newLikely
                              };
            await profileRepo.UpsertAsync(updated, ct);

            var excludedRepo = repositoryFactory.GetExcludedSymbolsRepository(profile);
            await excludedRepo.RemoveAsync(library, version, names, ct);

            result = JsonSerializer.Serialize(new
                                                  {
                                                      Library = library,
                                                      Version = version,
                                                      Demoted = demoted,
                                                      AlreadyInStoplist = alreadyInStoplist,
                                                      RemovedFromLikelySymbols = removedFromLikely,
                                                      Hints = new[] { "Call rescrub_library to apply the changes." }
                                                  }, smJsonOptions);
        }
        return result;
    }

    private static readonly JsonSerializerOptions smJsonOptions = new()
                                                                      {
                                                                          WriteIndented = true,
                                                                          Converters = { new JsonStringEnumConverter() }
                                                                      };

    private const int DefaultListLimit = 50;
}
