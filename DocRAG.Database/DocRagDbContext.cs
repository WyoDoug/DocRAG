// // DocRagDbContext.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using DocRAG.Core.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

#endregion

namespace DocRAG.Database;

/// <summary>
///     Provides typed access to MongoDB collections for the DocRAG system.
/// </summary>
public class DocRagDbContext
{
    public DocRagDbContext(IOptions<DocRagDbSettings> settings)
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
}
