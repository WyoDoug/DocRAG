// ExcludedSymbolsRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using MongoDB.Driver;

#endregion

namespace DocRAG.Database.Repositories;

/// <summary>
///     MongoDB-backed implementation of IExcludedSymbolsRepository.
///     Rejections are keyed by (LibraryId, Version, Name) via a composite
///     document id.
/// </summary>
public class ExcludedSymbolsRepository : IExcludedSymbolsRepository
{
    public ExcludedSymbolsRepository(DocRagDbContext context)
    {
        mContext = context;
    }

    private readonly DocRagDbContext mContext;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExcludedSymbol>> ListAsync(string libraryId,
                                                                string version,
                                                                SymbolRejectionReason? reason,
                                                                int limit,
                                                                CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var filterBase = mContext.ExcludedSymbols
                                 .Find(e => e.LibraryId == libraryId && e.Version == version);
        var filtered = reason.HasValue
                           ? mContext.ExcludedSymbols.Find(e => e.LibraryId == libraryId
                                                             && e.Version == version
                                                             && e.Reason == reason.Value)
                           : filterBase;

        var ordered = filtered.SortByDescending(e => e.ChunkCount).Limit(limit);
        var result = await ordered.ToListAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task UpsertManyAsync(IEnumerable<ExcludedSymbol> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var list = entries.ToList();
        if (list.Count > 0)
        {
            var models = list.Select(e => new ReplaceOneModel<ExcludedSymbol>(
                                              Builders<ExcludedSymbol>.Filter.Eq(x => x.Id, e.Id),
                                              e
                                          )
                                      { IsUpsert = true });
            await mContext.ExcludedSymbols.BulkWriteAsync(models, cancellationToken: ct);
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string libraryId,
                                  string version,
                                  IEnumerable<string> names,
                                  CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentNullException.ThrowIfNull(names);

        var nameList = names.ToList();
        if (nameList.Count > 0)
        {
            var filter = Builders<ExcludedSymbol>.Filter.And(
                Builders<ExcludedSymbol>.Filter.Eq(e => e.LibraryId, libraryId),
                Builders<ExcludedSymbol>.Filter.Eq(e => e.Version, version),
                Builders<ExcludedSymbol>.Filter.In(e => e.Name, nameList)
            );
            await mContext.ExcludedSymbols.DeleteManyAsync(filter, ct);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAllForLibraryAsync(string libraryId, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        await mContext.ExcludedSymbols
                      .DeleteManyAsync(e => e.LibraryId == libraryId && e.Version == version, ct);
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(string libraryId, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var count = await mContext.ExcludedSymbols
                                  .CountDocumentsAsync(e => e.LibraryId == libraryId && e.Version == version, cancellationToken: ct);
        var result = (int) count;
        return result;
    }
}
