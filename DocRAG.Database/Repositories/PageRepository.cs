// PageRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using MongoDB.Driver;

#endregion

namespace DocRAG.Database.Repositories;

/// <summary>
///     MongoDB implementation of page record data access.
/// </summary>
public class PageRepository : IPageRepository
{
    public PageRepository(DocRagDbContext context)
    {
        mContext = context;
    }

    private readonly DocRagDbContext mContext;

    /// <inheritdoc />
    public async Task UpsertPageAsync(PageRecord page, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        var filter = Builders<PageRecord>.Filter.And(Builders<PageRecord>.Filter.Eq(p => p.LibraryId, page.LibraryId),
                                                     Builders<PageRecord>.Filter.Eq(p => p.Version, page.Version),
                                                     Builders<PageRecord>.Filter.Eq(p => p.Url, page.Url)
                                                    );

        await mContext.Pages.ReplaceOneAsync(filter,
                                             page,
                                             new ReplaceOptions { IsUpsert = true },
                                             ct
                                            );
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PageRecord>> GetPagesAsync(string libraryId,
                                                               string version,
                                                               CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var filter = Builders<PageRecord>.Filter.And(Builders<PageRecord>.Filter.Eq(p => p.LibraryId, libraryId),
                                                     Builders<PageRecord>.Filter.Eq(p => p.Version, version)
                                                    );

        var pages = await mContext.Pages.Find(filter).ToListAsync(ct);
        return pages;
    }

    /// <inheritdoc />
    public async Task<PageRecord?> GetPageByUrlAsync(string libraryId,
                                                     string version,
                                                     string url,
                                                     CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(url);

        var filter = Builders<PageRecord>.Filter.And(Builders<PageRecord>.Filter.Eq(p => p.LibraryId, libraryId),
                                                     Builders<PageRecord>.Filter.Eq(p => p.Version, version),
                                                     Builders<PageRecord>.Filter.Eq(p => p.Url, url)
                                                    );

        var result = await mContext.Pages.Find(filter).FirstOrDefaultAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task<int> GetPageCountAsync(string libraryId,
                                             string version,
                                             CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var filter = Builders<PageRecord>.Filter.And(Builders<PageRecord>.Filter.Eq(p => p.LibraryId, libraryId),
                                                     Builders<PageRecord>.Filter.Eq(p => p.Version, version)
                                                    );

        var count = (int) await mContext.Pages.CountDocumentsAsync(filter, cancellationToken: ct);
        return count;
    }

    /// <inheritdoc />
    public async Task<long> DeleteAsync(string libraryId, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var filter = Builders<PageRecord>.Filter.And(
            Builders<PageRecord>.Filter.Eq(p => p.LibraryId, libraryId),
            Builders<PageRecord>.Filter.Eq(p => p.Version, version)
        );
        var result = await mContext.Pages.DeleteManyAsync(filter, ct);
        return result.DeletedCount;
    }
}
