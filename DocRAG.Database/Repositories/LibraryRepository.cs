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

    private const string ScrapeJobLibraryIdPath = "Job.LibraryId";

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

    /// <inheritdoc />
    public async Task<DeleteVersionResult> DeleteVersionAsync(string libraryId,
                                                              string version,
                                                              CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var versionFilter = Builders<LibraryVersionRecord>.Filter.And(
            Builders<LibraryVersionRecord>.Filter.Eq(v => v.LibraryId, libraryId),
            Builders<LibraryVersionRecord>.Filter.Eq(v => v.Version, version)
        );
        var versionsDeleted = (await mContext.LibraryVersions.DeleteManyAsync(versionFilter, ct)).DeletedCount;

        var remaining = await mContext.LibraryVersions
                                      .Find(Builders<LibraryVersionRecord>.Filter.Eq(v => v.LibraryId, libraryId))
                                      .SortByDescending(v => v.ScrapedAt)
                                      .ToListAsync(ct);

        bool libraryRowDeleted = false;
        string? repointedTo = null;

        if (remaining.Count == 0)
        {
            var libFilter = Builders<LibraryRecord>.Filter.Eq(l => l.Id, libraryId);
            var libDeleted = (await mContext.Libraries.DeleteOneAsync(libFilter, ct)).DeletedCount;
            libraryRowDeleted = libDeleted > 0;
        }
        else
        {
            var library = await GetLibraryAsync(libraryId, ct);
            if (library != null && library.CurrentVersion == version)
            {
                var newCurrent = remaining[0].Version;
                library.CurrentVersion = newCurrent;
                await UpsertLibraryAsync(library, ct);
                repointedTo = newCurrent;
            }
        }

        var result = new DeleteVersionResult(versionsDeleted, libraryRowDeleted, repointedTo);
        return result;
    }

    /// <inheritdoc />
    public async Task<long> DeleteAsync(string libraryId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);

        var versions = await mContext.LibraryVersions
                                     .Find(Builders<LibraryVersionRecord>.Filter.Eq(v => v.LibraryId, libraryId))
                                     .ToListAsync(ct);

        long total = 0;
        foreach (var v in versions)
        {
            var result = await DeleteVersionAsync(libraryId, v.Version, ct);
            total += result.VersionsDeleted;
        }

        var libFilter = Builders<LibraryRecord>.Filter.Eq(l => l.Id, libraryId);
        await mContext.Libraries.DeleteOneAsync(libFilter, ct);

        return total;
    }

    /// <inheritdoc />
    public async Task<RenameLibraryResponse> RenameAsync(string oldId, string newId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(oldId);
        ArgumentException.ThrowIfNullOrEmpty(newId);

        RenameLibraryResponse result;

        var existing = await GetLibraryAsync(oldId, ct);
        if (existing == null)
            result = new RenameLibraryResponse(RenameLibraryOutcome.NotFound, null);
        else
        {
            var collision = await GetLibraryAsync(newId, ct);
            if (collision != null)
                result = new RenameLibraryResponse(RenameLibraryOutcome.Collision, null);
            else
            {
                var counts = await ApplyRenameAsync(oldId, newId, ct);
                result = new RenameLibraryResponse(RenameLibraryOutcome.Renamed, counts);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task SetSuspectAsync(string libraryId, string version, IReadOnlyList<string> reasons, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentNullException.ThrowIfNull(reasons);

        var filter = Builders<LibraryVersionRecord>.Filter.And(
            Builders<LibraryVersionRecord>.Filter.Eq(v => v.LibraryId, libraryId),
            Builders<LibraryVersionRecord>.Filter.Eq(v => v.Version, version)
        );
        var update = Builders<LibraryVersionRecord>.Update
            .Set(v => v.Suspect, true)
            .Set(v => v.SuspectReasons, reasons)
            .Set(v => v.LastSuspectEvaluatedAt, DateTime.UtcNow);
        await mContext.LibraryVersions.UpdateOneAsync(filter, update, cancellationToken: ct);
    }

    /// <inheritdoc />
    public async Task ClearSuspectAsync(string libraryId, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var filter = Builders<LibraryVersionRecord>.Filter.And(
            Builders<LibraryVersionRecord>.Filter.Eq(v => v.LibraryId, libraryId),
            Builders<LibraryVersionRecord>.Filter.Eq(v => v.Version, version)
        );
        var update = Builders<LibraryVersionRecord>.Update
            .Set(v => v.Suspect, false)
            .Set(v => v.SuspectReasons, Array.Empty<string>())
            .Set(v => v.LastSuspectEvaluatedAt, DateTime.UtcNow);
        await mContext.LibraryVersions.UpdateOneAsync(filter, update, cancellationToken: ct);
    }

    private async Task<RenameLibraryResult> ApplyRenameAsync(string oldId, string newId, CancellationToken ct)
    {
        var libFilter = Builders<LibraryRecord>.Filter.Eq(l => l.Id, oldId);
        var libUpdate = Builders<LibraryRecord>.Update.Set(l => l.Id, newId);
        var libRes = await mContext.Libraries.UpdateOneAsync(libFilter, libUpdate, cancellationToken: ct);

        var verFilter = Builders<LibraryVersionRecord>.Filter.Eq(v => v.LibraryId, oldId);
        var verUpdate = Builders<LibraryVersionRecord>.Update.Set(v => v.LibraryId, newId);
        var verRes = await mContext.LibraryVersions.UpdateManyAsync(verFilter, verUpdate, cancellationToken: ct);

        var chunkFilter = Builders<DocChunk>.Filter.Eq(c => c.LibraryId, oldId);
        var chunkUpdate = Builders<DocChunk>.Update.Set(c => c.LibraryId, newId);
        var chunkRes = await mContext.Chunks.UpdateManyAsync(chunkFilter, chunkUpdate, cancellationToken: ct);

        var pageFilter = Builders<PageRecord>.Filter.Eq(p => p.LibraryId, oldId);
        var pageUpdate = Builders<PageRecord>.Update.Set(p => p.LibraryId, newId);
        var pageRes = await mContext.Pages.UpdateManyAsync(pageFilter, pageUpdate, cancellationToken: ct);

        var profileFilter = Builders<LibraryProfile>.Filter.Eq(p => p.LibraryId, oldId);
        var profileUpdate = Builders<LibraryProfile>.Update.Set(p => p.LibraryId, newId);
        var profileRes = await mContext.LibraryProfiles.UpdateManyAsync(profileFilter, profileUpdate, cancellationToken: ct);

        var indexFilter = Builders<LibraryIndex>.Filter.Eq(i => i.LibraryId, oldId);
        var indexUpdate = Builders<LibraryIndex>.Update.Set(i => i.LibraryId, newId);
        var indexRes = await mContext.LibraryIndexes.UpdateManyAsync(indexFilter, indexUpdate, cancellationToken: ct);

        var shardFilter = Builders<Bm25Shard>.Filter.Eq(s => s.LibraryId, oldId);
        var shardUpdate = Builders<Bm25Shard>.Update.Set(s => s.LibraryId, newId);
        var shardRes = await mContext.Bm25Shards.UpdateManyAsync(shardFilter, shardUpdate, cancellationToken: ct);

        var exFilter = Builders<ExcludedSymbol>.Filter.Eq(e => e.LibraryId, oldId);
        var exUpdate = Builders<ExcludedSymbol>.Update.Set(e => e.LibraryId, newId);
        var exRes = await mContext.ExcludedSymbols.UpdateManyAsync(exFilter, exUpdate, cancellationToken: ct);

        var jobFilter = Builders<ScrapeJobRecord>.Filter.Eq(ScrapeJobLibraryIdPath, oldId);
        var jobUpdate = Builders<ScrapeJobRecord>.Update.Set(ScrapeJobLibraryIdPath, newId);
        var jobRes = await mContext.ScrapeJobs.UpdateManyAsync(jobFilter, jobUpdate, cancellationToken: ct);

        var result = new RenameLibraryResult(libRes.ModifiedCount,
                                             verRes.ModifiedCount,
                                             chunkRes.ModifiedCount,
                                             pageRes.ModifiedCount,
                                             profileRes.ModifiedCount,
                                             indexRes.ModifiedCount,
                                             shardRes.ModifiedCount,
                                             exRes.ModifiedCount,
                                             jobRes.ModifiedCount);
        return result;
    }
}
