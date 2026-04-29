// LibraryIndexRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using MongoDB.Driver;

#endregion

namespace SaddleRAG.Database.Repositories;

/// <summary>
///     MongoDB-backed implementation of ILibraryIndexRepository. Stores the
///     BM25 + CodeFenceSymbols + Manifest bundle for each (library, version).
/// </summary>
public class LibraryIndexRepository : ILibraryIndexRepository
{
    public LibraryIndexRepository(SaddleRagDbContext context)
    {
        mContext = context;
    }

    private readonly SaddleRagDbContext mContext;

    /// <inheritdoc />
    public async Task<LibraryIndex?> GetAsync(string libraryId, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var id = MakeId(libraryId, version);
        var result = await mContext.LibraryIndexes
                                   .Find(i => i.Id == id)
                                   .FirstOrDefaultAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task UpsertAsync(LibraryIndex index, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(index);

        await mContext.LibraryIndexes.ReplaceOneAsync(i => i.Id == index.Id,
                                                      index,
                                                      new ReplaceOptions { IsUpsert = true },
                                                      ct
                                                     );
    }

    /// <inheritdoc />
    public async Task<long> DeleteAsync(string libraryId, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var id = MakeId(libraryId, version);
        var result = await mContext.LibraryIndexes.DeleteOneAsync(i => i.Id == id, ct);
        return result.DeletedCount;
    }

    /// <summary>
    ///     Compose the document id used as the primary key for an index bundle.
    /// </summary>
    public static string MakeId(string libraryId, string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        return $"{libraryId}/{version}";
    }
}
