// LibraryProfileRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using MongoDB.Driver;

#endregion

namespace DocRAG.Database.Repositories;

/// <summary>
///     MongoDB-backed implementation of ILibraryProfileRepository.
///     Profiles are keyed by (LibraryId, Version) via a composite document id.
/// </summary>
public class LibraryProfileRepository : ILibraryProfileRepository
{
    public LibraryProfileRepository(DocRagDbContext context)
    {
        mContext = context;
    }

    private readonly DocRagDbContext mContext;

    /// <inheritdoc />
    public async Task<LibraryProfile?> GetAsync(string libraryId, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var id = MakeId(libraryId, version);
        var result = await mContext.LibraryProfiles
                                   .Find(p => p.Id == id)
                                   .FirstOrDefaultAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task UpsertAsync(LibraryProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        await mContext.LibraryProfiles.ReplaceOneAsync(p => p.Id == profile.Id,
                                                       profile,
                                                       new ReplaceOptions { IsUpsert = true },
                                                       ct
                                                      );
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string libraryId, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var id = MakeId(libraryId, version);
        await mContext.LibraryProfiles.DeleteOneAsync(p => p.Id == id, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LibraryProfile>> ListAllAsync(CancellationToken ct = default)
    {
        var profiles = await mContext.LibraryProfiles
                                     .Find(FilterDefinition<LibraryProfile>.Empty)
                                     .ToListAsync(ct);
        return profiles;
    }

    /// <summary>
    ///     Compose the document id used as the primary key for a profile.
    ///     Public so callers building new LibraryProfile records can use the
    ///     same convention without duplicating the format.
    /// </summary>
    public static string MakeId(string libraryId, string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        return $"{libraryId}/{version}";
    }
}
