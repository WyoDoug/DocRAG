// SaddleRagDbContext.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Core.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;

#endregion

namespace SaddleRAG.Database;

/// <summary>
///     Provides typed access to MongoDB collections for the SaddleRAG system.
/// </summary>
public class SaddleRagDbContext
{
    public SaddleRagDbContext(IOptions<SaddleRagDbSettings> settings)
    {
        (var connectionString, var databaseName) = settings.Value.Resolve();
        var client = new MongoClient(connectionString);
        mDatabase = client.GetDatabase(databaseName);
    }

    public IMongoCollection<LibraryRecord> Libraries =>
        mDatabase.GetCollection<LibraryRecord>(CollectionLibraries);

    public IMongoCollection<LibraryVersionRecord> LibraryVersions =>
        mDatabase.GetCollection<LibraryVersionRecord>(CollectionLibraryVersions);

    public IMongoCollection<PageRecord> Pages =>
        mDatabase.GetCollection<PageRecord>(CollectionPages);

    public IMongoCollection<DocChunk> Chunks =>
        mDatabase.GetCollection<DocChunk>(CollectionChunks);

    public IMongoCollection<VersionDiffRecord> VersionDiffs =>
        mDatabase.GetCollection<VersionDiffRecord>(CollectionVersionDiffs);

    public IMongoCollection<ProjectProfile> ProjectProfiles =>
        mDatabase.GetCollection<ProjectProfile>(CollectionProjectProfiles);

    public IMongoCollection<ScrapeJobRecord> ScrapeJobs =>
        mDatabase.GetCollection<ScrapeJobRecord>(CollectionScrapeJobs);

    public IMongoCollection<LibraryProfile> LibraryProfiles =>
        mDatabase.GetCollection<LibraryProfile>(CollectionLibraryProfiles);

    public IMongoCollection<LibraryIndex> LibraryIndexes =>
        mDatabase.GetCollection<LibraryIndex>(CollectionLibraryIndexes);

    public IMongoCollection<Bm25Shard> Bm25Shards =>
        mDatabase.GetCollection<Bm25Shard>(CollectionBm25Shards);

    public IMongoCollection<ExcludedSymbol> ExcludedSymbols =>
        mDatabase.GetCollection<ExcludedSymbol>(CollectionExcludedSymbols);

    /// <summary>
    ///     GridFS bucket for spilled BM25 payloads (per-term postings or
    ///     entire shards) that exceed the inline 16MB Mongo document
    ///     limit. Reader and writer talk to this bucket via the shard
    ///     repository — callers don't construct it directly.
    /// </summary>
    public IGridFSBucket Bm25Bucket =>
        new GridFSBucket(mDatabase, new GridFSBucketOptions { BucketName = Bm25BucketName });

    private readonly IMongoDatabase mDatabase;

    /// <summary>
    ///     Ensures required indexes exist on all collections.
    /// </summary>
    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        // Pages: compound unique index on LibraryId + Version + Url
        var pageKeys = Builders<PageRecord>.IndexKeys;
        await Pages.Indexes.CreateOneAsync(new CreateIndexModel<PageRecord>(pageKeys.Combine(pageKeys.Ascending(p => p.LibraryId),
                                                                                             pageKeys.Ascending(p => p.Version),
                                                                                             pageKeys.Ascending(p => p.Url)
                                                                                            ),
                                                                            new CreateIndexOptions { Unique = true }
                                                                           ),
                                           cancellationToken: ct
                                          );

        // Chunks: compound index on LibraryId + Version + Category
        var chunkKeys = Builders<DocChunk>.IndexKeys;
        await Chunks.Indexes.CreateOneAsync(new CreateIndexModel<DocChunk>(chunkKeys.Combine(chunkKeys.Ascending(c => c.LibraryId),
                                                                                             chunkKeys.Ascending(c => c.Version),
                                                                                             chunkKeys.Ascending(c => c.Category)
                                                                                            )
                                                                          ),
                                            cancellationToken: ct
                                           );

        // Chunks: sparse index on QualifiedName for API reference lookups
        await Chunks.Indexes.CreateOneAsync(new CreateIndexModel<DocChunk>(chunkKeys.Ascending(c => c.QualifiedName),
                                                                           new CreateIndexOptions { Sparse = true }
                                                                          ),
                                            cancellationToken: ct
                                           );

        // Chunks: compound index on LibraryId + Version + ParserVersion for STALE detection
        await Chunks.Indexes.CreateOneAsync(new CreateIndexModel<DocChunk>(chunkKeys.Combine(chunkKeys.Ascending(c => c.LibraryId),
                                                                                             chunkKeys.Ascending(c => c.Version),
                                                                                             chunkKeys.Ascending(c => c.ParserVersion)
                                                                                            )
                                                                          ),
                                            cancellationToken: ct
                                           );

        // LibraryProfiles: compound index on LibraryId + Version
        var profileKeys = Builders<LibraryProfile>.IndexKeys;
        await LibraryProfiles.Indexes.CreateOneAsync(new CreateIndexModel<LibraryProfile>(profileKeys.Combine(profileKeys.Ascending(p => p.LibraryId),
                                                                                                              profileKeys.Ascending(p => p.Version)
                                                                                                             )
                                                                                         ),
                                                     cancellationToken: ct
                                                    );

        // LibraryIndexes: compound index on LibraryId + Version
        var indexKeys = Builders<LibraryIndex>.IndexKeys;
        await LibraryIndexes.Indexes.CreateOneAsync(new CreateIndexModel<LibraryIndex>(indexKeys.Combine(indexKeys.Ascending(i => i.LibraryId),
                                                                                                         indexKeys.Ascending(i => i.Version)
                                                                                                        )
                                                                                      ),
                                                    cancellationToken: ct
                                                   );

        // Bm25Shards: compound index on LibraryId + Version + ShardIndex
        // for batch-load by (lib, ver) and pinpoint lookup by shard.
        var shardKeys = Builders<Bm25Shard>.IndexKeys;
        await Bm25Shards.Indexes.CreateOneAsync(new CreateIndexModel<Bm25Shard>(shardKeys.Combine(shardKeys.Ascending(s => s.LibraryId),
                                                                                                   shardKeys.Ascending(s => s.Version),
                                                                                                   shardKeys.Ascending(s => s.ShardIndex)
                                                                                                  )
                                                                               ),
                                                cancellationToken: ct
                                               );

        // ExcludedSymbols: compound on (LibraryId, Version, Reason) for the
        // list_excluded_symbols reason filter, plus (LibraryId, Version, Name)
        // for fast remove-by-name when the LLM promotes/demotes tokens.
        var excludedKeys = Builders<ExcludedSymbol>.IndexKeys;
        await ExcludedSymbols.Indexes.CreateOneAsync(new CreateIndexModel<ExcludedSymbol>(excludedKeys.Combine(excludedKeys.Ascending(e => e.LibraryId),
                                                                                                                excludedKeys.Ascending(e => e.Version),
                                                                                                                excludedKeys.Ascending(e => e.Reason)
                                                                                                               )
                                                                                          ),
                                                     cancellationToken: ct
                                                    );
        await ExcludedSymbols.Indexes.CreateOneAsync(new CreateIndexModel<ExcludedSymbol>(excludedKeys.Combine(excludedKeys.Ascending(e => e.LibraryId),
                                                                                                                excludedKeys.Ascending(e => e.Version),
                                                                                                                excludedKeys.Ascending(e => e.Name)
                                                                                                               )
                                                                                          ),
                                                     cancellationToken: ct
                                                    );
    }

    private const string CollectionLibraries = "libraries";
    private const string CollectionLibraryVersions = "libraryVersions";
    private const string CollectionPages = "pages";
    private const string CollectionChunks = "chunks";
    private const string CollectionVersionDiffs = "versionDiffs";
    private const string CollectionProjectProfiles = "projectProfiles";
    private const string CollectionScrapeJobs = "scrapeJobs";
    private const string CollectionLibraryProfiles = "libraryProfiles";
    private const string CollectionLibraryIndexes = "libraryIndexes";
    private const string CollectionBm25Shards = "bm25Shards";
    private const string CollectionExcludedSymbols = "library_excluded_symbols";
    private const string Bm25BucketName = "bm25";
}
