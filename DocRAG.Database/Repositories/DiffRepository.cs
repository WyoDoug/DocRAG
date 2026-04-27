// DiffRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using MongoDB.Driver;

#endregion

namespace DocRAG.Database.Repositories;

/// <summary>
///     MongoDB implementation of version diff data access.
/// </summary>
public class DiffRepository : IDiffRepository
{
    public DiffRepository(DocRagDbContext context)
    {
        mContext = context;
    }

    private readonly DocRagDbContext mContext;

    /// <inheritdoc />
    public async Task UpsertDiffAsync(VersionDiffRecord diff, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(diff);

        await mContext.VersionDiffs.ReplaceOneAsync(d => d.Id == diff.Id,
                                                    diff,
                                                    new ReplaceOptions { IsUpsert = true },
                                                    ct
                                                   );
    }

    /// <inheritdoc />
    public async Task<VersionDiffRecord?> GetDiffAsync(string libraryId,
                                                       string fromVersion,
                                                       string toVersion,
                                                       CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(fromVersion);
        ArgumentException.ThrowIfNullOrEmpty(toVersion);

        var id = $"{libraryId}/{fromVersion}-to-{toVersion}";
        var result = await mContext.VersionDiffs
                                   .Find(d => d.Id == id)
                                   .FirstOrDefaultAsync(ct);
        return result;
    }
}
