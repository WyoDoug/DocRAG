// RepositoryFactory.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Interfaces;

#endregion

namespace DocRAG.Database.Repositories;

/// <summary>
///     Creates per-profile repository instances on demand.
///     Used by MCP tools that need to query a specific user's database.
/// </summary>
public class RepositoryFactory
{
    public RepositoryFactory(DocRagDbContextFactory contextFactory)
    {
        mContextFactory = contextFactory;
    }

    private readonly DocRagDbContextFactory mContextFactory;

    /// <summary>
    ///     Get a library repository for the specified profile.
    ///     Null profile uses the default.
    /// </summary>
    public virtual ILibraryRepository GetLibraryRepository(string? profile = null)
    {
        var context = mContextFactory.GetForProfile(profile);
        var result = new LibraryRepository(context);
        return result;
    }

    /// <summary>
    ///     Get a page repository for the specified profile.
    /// </summary>
    public virtual IPageRepository GetPageRepository(string? profile = null)
    {
        var context = mContextFactory.GetForProfile(profile);
        var result = new PageRepository(context);
        return result;
    }

    /// <summary>
    ///     Get a chunk repository for the specified profile.
    /// </summary>
    public virtual IChunkRepository GetChunkRepository(string? profile = null)
    {
        var context = mContextFactory.GetForProfile(profile);
        var result = new ChunkRepository(context);
        return result;
    }

    /// <summary>
    ///     Get a diff repository for the specified profile.
    /// </summary>
    public virtual IDiffRepository GetDiffRepository(string? profile = null)
    {
        var context = mContextFactory.GetForProfile(profile);
        var result = new DiffRepository(context);
        return result;
    }

    /// <summary>
    ///     Get a scrape job repository for the specified profile.
    /// </summary>
    public virtual IScrapeJobRepository GetScrapeJobRepository(string? profile = null)
    {
        var context = mContextFactory.GetForProfile(profile);
        var result = new ScrapeJobRepository(context);
        return result;
    }

    /// <summary>
    ///     Get a library-profile repository for the specified database profile.
    ///     Stores the per-(library, version) reconnaissance results.
    /// </summary>
    public virtual ILibraryProfileRepository GetLibraryProfileRepository(string? profile = null)
    {
        var context = mContextFactory.GetForProfile(profile);
        var result = new LibraryProfileRepository(context);
        return result;
    }

    /// <summary>
    ///     Get a library-index repository for the specified database profile.
    ///     Stores BM25 stats + CodeFenceSymbols + Manifest per (library, version).
    /// </summary>
    public virtual ILibraryIndexRepository GetLibraryIndexRepository(string? profile = null)
    {
        var context = mContextFactory.GetForProfile(profile);
        var result = new LibraryIndexRepository(context);
        return result;
    }

    /// <summary>
    ///     Get a BM25 shard repository for the specified database profile.
    ///     Stores per-shard postings with per-term and per-shard GridFS
    ///     spill for any payload exceeding the inline 16MB Mongo limit.
    /// </summary>
    public virtual IBm25ShardRepository GetBm25ShardRepository(string? profile = null)
    {
        var context = mContextFactory.GetForProfile(profile);
        var result = new Bm25ShardRepository(context);
        return result;
    }

    /// <summary>
    ///     Get an excluded-symbols repository for the specified database
    ///     profile. Stores per-(library, version) extractor rejections
    ///     captured during rescrub.
    /// </summary>
    public virtual IExcludedSymbolsRepository GetExcludedSymbolsRepository(string? profile = null)
    {
        var context = mContextFactory.GetForProfile(profile);
        var result = new ExcludedSymbolsRepository(context);
        return result;
    }
}
