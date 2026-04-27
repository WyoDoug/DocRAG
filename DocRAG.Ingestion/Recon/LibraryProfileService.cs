// LibraryProfileService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using DocRAG.Database.Repositories;
using Microsoft.Extensions.Logging;

#endregion

namespace DocRAG.Ingestion.Recon;

/// <summary>
///     Validates, hashes, and persists LibraryProfile records produced by
///     reconnaissance. The profile hash is what LibraryManifest stores as
///     LastProfileHash so rescrub auto-detect can decide whether
///     classification needs to re-run when the profile changes.
///
///     The repository is taken per-call rather than via constructor so the
///     same singleton can dispatch to multiple database profiles
///     (multi-user MCP support).
/// </summary>
public class LibraryProfileService
{
    public LibraryProfileService(ILogger<LibraryProfileService> logger)
    {
        mLogger = logger;
    }

    private readonly ILogger<LibraryProfileService> mLogger;

    /// <summary>
    ///     Validate a profile and persist it via the supplied repository.
    ///     Throws if validation fails. Idempotent — replaces any existing
    ///     profile for the same (LibraryId, Version).
    /// </summary>
    public async Task<LibraryProfile> SaveAsync(ILibraryProfileRepository repository,
                                                LibraryProfile profile,
                                                CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(profile);

        Validate(profile);

        var normalized = Normalize(profile);
        var withCarryForward = await ApplyStoplistCarryForwardAsync(repository, normalized, ct);

        await repository.UpsertAsync(withCarryForward, ct);
        mLogger.LogInformation("Saved library profile for {LibraryId}/{Version} (source={Source}, confidence={Confidence:F2}, stoplist={StoplistCount})",
                               withCarryForward.LibraryId,
                               withCarryForward.Version,
                               withCarryForward.Source,
                               withCarryForward.Confidence,
                               withCarryForward.Stoplist.Count
                              );
        return withCarryForward;
    }

    /// <summary>
    ///     If the incoming profile has an empty Stoplist and a prior profile
    ///     for the same LibraryId (any other version) has a non-empty
    ///     Stoplist, copy the most-recent prior Stoplist forward. Lets the
    ///     LLM's curation work survive a library version bump without
    ///     re-doing it. Non-empty incoming Stoplists are never overridden.
    /// </summary>
    private static async Task<LibraryProfile> ApplyStoplistCarryForwardAsync(ILibraryProfileRepository repository,
                                                                              LibraryProfile profile,
                                                                              CancellationToken ct)
    {
        LibraryProfile result = profile;
        if (profile.Stoplist.Count == 0)
        {
            var all = await repository.ListAllAsync(ct);
            var prior = all.Where(p => string.Equals(p.LibraryId, profile.LibraryId, StringComparison.Ordinal)
                                    && !string.Equals(p.Version, profile.Version, StringComparison.Ordinal)
                                    && p.Stoplist.Count > 0)
                           .OrderByDescending(p => p.CreatedUtc)
                           .FirstOrDefault();
            if (prior != null)
                result = profile with { Stoplist = prior.Stoplist };
        }
        return result;
    }

    /// <summary>
    ///     Compute a stable hash of the profile's content for manifest
    ///     tracking. Hash deliberately excludes CreatedUtc and Source so
    ///     two profiles with identical structural content but different
    ///     metadata produce the same hash. Also excludes Stoplist — it is
    ///     user-curated state, and changes to it should not trigger
    ///     reclassification (which is what manifest-hash drift drives).
    /// </summary>
    public static string ComputeHash(LibraryProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var canonical = new
                            {
                                profile.SchemaVersion,
                                profile.LibraryId,
                                profile.Version,
                                Languages = profile.Languages.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                                profile.Casing,
                                Separators = profile.Separators.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                                CallableShapes = profile.CallableShapes.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                                LikelySymbols = profile.LikelySymbols.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                                profile.CanonicalInventoryUrl
                            };

        var json = JsonSerializer.Serialize(canonical, smHashJsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    ///     Builds a LibraryProfile from caller-supplied raw fields. Used by
    ///     the submit_library_profile MCP tool and by CliReconFallback.
    ///     Stamps CreatedUtc and the canonical Id; does not persist.
    /// </summary>
    public static LibraryProfile Build(string libraryId,
                                       string version,
                                       IReadOnlyList<string> languages,
                                       CasingConventions casing,
                                       IReadOnlyList<string> separators,
                                       IReadOnlyList<string> callableShapes,
                                       IReadOnlyList<string> likelySymbols,
                                       string? canonicalInventoryUrl,
                                       float confidence,
                                       string source)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentNullException.ThrowIfNull(languages);
        ArgumentNullException.ThrowIfNull(casing);
        ArgumentNullException.ThrowIfNull(separators);
        ArgumentNullException.ThrowIfNull(callableShapes);
        ArgumentNullException.ThrowIfNull(likelySymbols);
        ArgumentException.ThrowIfNullOrEmpty(source);

        var result = new LibraryProfile
                         {
                             Id = LibraryProfileRepository.MakeId(libraryId, version),
                             LibraryId = libraryId,
                             Version = version,
                             Languages = languages,
                             Casing = casing,
                             Separators = separators,
                             CallableShapes = callableShapes,
                             LikelySymbols = likelySymbols,
                             CanonicalInventoryUrl = canonicalInventoryUrl,
                             Confidence = confidence,
                             Source = source,
                             CreatedUtc = DateTime.UtcNow
                         };

        return result;
    }

    private static void Validate(LibraryProfile profile)
    {
        if (string.IsNullOrEmpty(profile.LibraryId))
            throw new ArgumentException("LibraryId must not be empty", nameof(profile));

        if (string.IsNullOrEmpty(profile.Version))
            throw new ArgumentException("Version must not be empty", nameof(profile));

        if (string.IsNullOrEmpty(profile.Source))
            throw new ArgumentException("Source must not be empty", nameof(profile));

        if (profile.Confidence < 0f || profile.Confidence > 1f)
            throw new ArgumentException($"Confidence must be in [0,1], got {profile.Confidence}", nameof(profile));

        if (profile.SchemaVersion < 1)
            throw new ArgumentException($"SchemaVersion must be >= 1, got {profile.SchemaVersion}", nameof(profile));
    }

    private static LibraryProfile Normalize(LibraryProfile profile)
    {
        var expectedId = LibraryProfileRepository.MakeId(profile.LibraryId, profile.Version);

        var result = string.Equals(profile.Id, expectedId, StringComparison.Ordinal)
                         ? profile
                         : profile with { Id = expectedId };
        return result;
    }

    private static readonly JsonSerializerOptions smHashJsonOptions = new()
                                                                          {
                                                                              WriteIndented = false
                                                                          };
}
