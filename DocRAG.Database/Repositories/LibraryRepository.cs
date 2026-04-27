// LibraryRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using MongoDB.Driver;

#endregion

namespace DocRAG.Database.Repositories;

/// <summary>
///     MongoDB implementation of library and version record data access.
/// </summary>
public class LibraryRepository : ILibraryRepository
{
    public LibraryRepository(DocRagDbContext context)
    {
        mContext = context;
    }

    private readonly DocRagDbContext mContext;

    /// <inheritdoc />
    public async Task<IReadOnlyList<LibraryRecord>> GetAllLibrariesAsync(CancellationToken ct = default)
    {
        var libraries = await mContext.Libraries
                                      .Find(FilterDefinition<LibraryRecord>.Empty)
                                      .ToListAsync(ct);
        return libraries;
    }

    /// <inheritdoc />
    public async Task<LibraryRecord?> GetLibraryAsync(string libraryId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);

        var result = await mContext.Libraries
                                   .Find(l => l.Id == libraryId)
                                   .FirstOrDefaultAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task UpsertLibraryAsync(LibraryRecord library, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(library);

        await mContext.Libraries.ReplaceOneAsync(l => l.Id == library.Id,
                                                 library,
                                                 new ReplaceOptions { IsUpsert = true },
                                                 ct
                                                );
    }

    /// <inheritdoc />
    public async Task<LibraryVersionRecord?> GetVersionAsync(string libraryId,
                                                             string version,
                                                             CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var id = $"{libraryId}/{version}";
        var result = await mContext.LibraryVersions
                                   .Find(v => v.Id == id)
                                   .FirstOrDefaultAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task UpsertVersionAsync(LibraryVersionRecord versionRecord, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(versionRecord);

        await mContext.LibraryVersions.ReplaceOneAsync(v => v.Id == versionRecord.Id,
                                                       versionRecord,
                                                       new ReplaceOptions { IsUpsert = true },
                                                       ct
                                                      );
    }
}
