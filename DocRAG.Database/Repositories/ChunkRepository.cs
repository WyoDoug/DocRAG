// // ChunkRepository.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using MongoDB.Bson;
using MongoDB.Driver;

#endregion

namespace DocRAG.Database.Repositories;

/// <summary>
///     MongoDB implementation of chunk data access.
///     Vector search is handled by IVectorSearchProvider, not here.
/// </summary>
public class ChunkRepository : IChunkRepository
{
    public ChunkRepository(DocRagDbContext context)
    {
        mContext = context;
    }

    private readonly DocRagDbContext mContext;

    /// <inheritdoc />
    public async Task InsertChunksAsync(IReadOnlyList<DocChunk> chunks, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        if (chunks.Count > 0)
            await mContext.Chunks.InsertManyAsync(chunks, cancellationToken: ct);
    }

    /// <inheritdoc />
    public async Task UpsertChunksAsync(IReadOnlyList<DocChunk> chunks, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        var task = chunks.Count > 0
                       ? UpsertChunksBulkAsync(chunks, ct)
                       : Task.CompletedTask;

        await task;
    }

    /// <inheritdoc />
    public async Task DeleteChunksAsync(string libraryId, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var filter = Builders<DocChunk>.Filter.And(Builders<DocChunk>.Filter.Eq(c => c.LibraryId, libraryId),
                                                   Builders<DocChunk>.Filter.Eq(c => c.Version, version)
                                                  );

        await mContext.Chunks.DeleteManyAsync(filter, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocChunk>> GetChunksAsync(string libraryId,
                                                              string version,
                                                              CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var filter = Builders<DocChunk>.Filter.And(Builders<DocChunk>.Filter.Eq(c => c.LibraryId, libraryId),
                                                   Builders<DocChunk>.Filter.Eq(c => c.Version, version)
                                                  );

        var chunks = await mContext.Chunks.Find(filter).ToListAsync(ct);
        return chunks;
    }

    /// <inheritdoc />
    public async Task<int> GetChunkCountAsync(string libraryId, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var filter = Builders<DocChunk>.Filter.And(Builders<DocChunk>.Filter.Eq(c => c.LibraryId, libraryId),
                                                   Builders<DocChunk>.Filter.Eq(c => c.Version, version)
                                                  );

        var count = (int) await mContext.Chunks.CountDocumentsAsync(filter, cancellationToken: ct);
        return count;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocChunk>> FindByQualifiedNameAsync(string libraryId,
                                                                        string version,
                                                                        string qualifiedName,
                                                                        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(qualifiedName);

        var filterBuilder = Builders<DocChunk>.Filter;

        // Try exact match first
        var exactFilter = filterBuilder.And(filterBuilder.Eq(c => c.LibraryId, libraryId),
                                            filterBuilder.Eq(c => c.Version, version),
                                            filterBuilder.Eq(c => c.QualifiedName, qualifiedName)
                                           );

        var results = await mContext.Chunks.Find(exactFilter).ToListAsync(ct);

        if (results.Count == 0)
        {
            // Fall back to case-insensitive regex contains
            var regexFilter = filterBuilder.And(filterBuilder.Eq(c => c.LibraryId, libraryId),
                                                filterBuilder.Eq(c => c.Version, version),
                                                filterBuilder.Regex(c => c.QualifiedName,
                                                                    new BsonRegularExpression(qualifiedName, "i")
                                                                   )
                                               );

            results = await mContext.Chunks.Find(regexFilter).ToListAsync(ct);
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetQualifiedNamesAsync(string libraryId,
                                                                    string version,
                                                                    string? filter = null,
                                                                    CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var filterBuilder = Builders<DocChunk>.Filter;
        var baseFilter = filterBuilder.And(filterBuilder.Eq(c => c.LibraryId, libraryId),
                                           filterBuilder.Eq(c => c.Version, version),
                                           filterBuilder.Ne(c => c.QualifiedName, value: null)
                                          );

        if (!string.IsNullOrWhiteSpace(filter))
        {
            baseFilter = filterBuilder.And(baseFilter,
                                           filterBuilder.Regex(c => c.QualifiedName,
                                                               new BsonRegularExpression(filter, "i")
                                                              )
                                          );
        }

        var chunks = await mContext.Chunks
                                   .Find(baseFilter)
                                   .Project(c => c.QualifiedName ?? string.Empty)
                                   .ToListAsync(ct);

        var distinct = chunks
                       .Where(n => n.Length > 0)
                       .Distinct()
                       .OrderBy(n => n)
                       .ToList();
        return distinct;
    }

    /// <inheritdoc />
    public async Task<long> UpdateCategoryByPageUrlAsync(string libraryId,
                                                         string version,
                                                         string pageUrl,
                                                         DocCategory newCategory,
                                                         CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(pageUrl);

        var filter = Builders<DocChunk>.Filter.And(Builders<DocChunk>.Filter.Eq(c => c.LibraryId, libraryId),
                                                   Builders<DocChunk>.Filter.Eq(c => c.Version, version),
                                                   Builders<DocChunk>.Filter.Eq(c => c.PageUrl, pageUrl)
                                                  );

        var update = Builders<DocChunk>.Update.Set(c => c.Category, newCategory);
        var result = await mContext.Chunks.UpdateManyAsync(filter, update, cancellationToken: ct);
        return result.ModifiedCount;
    }

    /// <inheritdoc />
    public async Task<bool> HasStaleChunksAsync(string libraryId,
                                                string version,
                                                int currentParserVersion,
                                                CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var filter = Builders<DocChunk>.Filter.And(Builders<DocChunk>.Filter.Eq(c => c.LibraryId, libraryId),
                                                   Builders<DocChunk>.Filter.Eq(c => c.Version, version),
                                                   Builders<DocChunk>.Filter.Lt(c => c.ParserVersion,
                                                                                currentParserVersion
                                                                               )
                                                  );

        var match = await mContext.Chunks
                                  .Find(filter)
                                  .Limit(1)
                                  .FirstOrDefaultAsync(ct);
        return match != null;
    }

    private async Task UpsertChunksBulkAsync(IReadOnlyList<DocChunk> chunks, CancellationToken ct)
    {
        var bulkOps = chunks.Select(chunk =>
                                    {
                                        var filter = Builders<DocChunk>.Filter.Eq(c => c.Id, chunk.Id);
                                        return new ReplaceOneModel<DocChunk>(filter, chunk) { IsUpsert = true };
                                    }
                                   )
                            .ToList();

        await mContext.Chunks.BulkWriteAsync(bulkOps, cancellationToken: ct);
    }
}
