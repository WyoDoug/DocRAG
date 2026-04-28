// ChunkRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

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
    public async Task<long> DeleteChunksAsync(string libraryId, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var filter = Builders<DocChunk>.Filter.And(Builders<DocChunk>.Filter.Eq(c => c.LibraryId, libraryId),
                                                   Builders<DocChunk>.Filter.Eq(c => c.Version, version)
                                                  );

        var result = await mContext.Chunks.DeleteManyAsync(filter, ct);
        return result.DeletedCount;
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

        var chunks = await GetChunksAsync(libraryId, version, ct);
        var names = ProjectTypeNames(chunks);
        var filtered = ApplyFilter(names, filter);
        var distinct = filtered.Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
        return distinct;
    }

    private static IEnumerable<string> ProjectTypeNames(IReadOnlyList<DocChunk> chunks)
    {
        // For v2+ chunks: return Symbols[] entries with Kind == Type.
        // For legacy v1 chunks: fall back to QualifiedName so the tool stays useful
        // until a rescrub bumps them. Each chunk yields zero or more names.
        var v2Names = chunks
                      .Where(c => c.ParserVersion >= ParserVersionV2 && c.Symbols.Count > 0)
                      .SelectMany(c => c.Symbols.Where(s => s.Kind == SymbolKind.Type).Select(s => s.Name));

        var legacyNames = chunks
                          .Where(c => c.ParserVersion < ParserVersionV2)
                          .Select(c => c.QualifiedName ?? string.Empty)
                          .Where(n => !string.IsNullOrEmpty(n));

        var result = v2Names.Concat(legacyNames).Where(n => !string.IsNullOrEmpty(n));
        return result;
    }

    private static IEnumerable<string> ApplyFilter(IEnumerable<string> names, string? filter)
    {
        var result = string.IsNullOrWhiteSpace(filter)
                         ? names
                         : names.Where(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetSymbolsAsync(string libraryId,
                                                             string version,
                                                             SymbolKind kind,
                                                             string? filter = null,
                                                             CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var chunks = await GetChunksAsync(libraryId, version, ct);
        var names = chunks
                    .Where(c => c.ParserVersion >= ParserVersionV2 && c.Symbols.Count > 0)
                    .SelectMany(c => c.Symbols.Where(s => s.Kind == kind).Select(s => s.Name))
                    .Where(n => !string.IsNullOrEmpty(n));

        var filtered = ApplyFilter(names, filter);
        var distinct = filtered.Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
        return distinct;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Symbol>> GetAllSymbolsAsync(string libraryId,
                                                                string version,
                                                                string? filter = null,
                                                                CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var chunks = await GetChunksAsync(libraryId, version, ct);
        var seen = new HashSet<(string Name, SymbolKind Kind)>();
        var symbols = new List<Symbol>();

        foreach (var chunk in chunks)
        {
            var filtered = string.IsNullOrEmpty(filter)
                               ? chunk.Symbols
                               : chunk.Symbols.Where(s => s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                                             .ToList();

            foreach (var s in filtered)
            {
                var key = (s.Name, s.Kind);
                if (seen.Add(key))
                    symbols.Add(s);
            }
        }

        var result = (IReadOnlyList<Symbol>) symbols;
        return result;
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

    private const int ParserVersionV2 = 2;
}
