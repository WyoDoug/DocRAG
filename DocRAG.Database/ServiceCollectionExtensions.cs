// ServiceCollectionExtensions.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using DocRAG.Database.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson.Serialization;

#endregion

namespace DocRAG.Database;

/// <summary>
///     Registers DocRAG MongoDB services into the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Adds DocRAG MongoDB database services with profile support.
    /// </summary>
    public static IServiceCollection AddDocRagDatabase(this IServiceCollection services,
                                                       IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        RegisterClassMaps();

        services.Configure<DocRagDbSettings>(configuration.GetSection(DocRagDbSettings.SectionName));

        // Factory enables per-profile context creation (multi-user MCP support)
        services.AddSingleton<DocRagDbContextFactory>();
        services.AddSingleton<RepositoryFactory>();

        // Default-profile singletons (used by ingestion and the default MCP path)
        services.AddSingleton<DocRagDbContext>(sp =>
                                                   sp.GetRequiredService<DocRagDbContextFactory>().GetDefault()
                                              );
        services.AddSingleton<ILibraryRepository, LibraryRepository>();
        services.AddSingleton<IPageRepository, PageRepository>();
        services.AddSingleton<IChunkRepository, ChunkRepository>();
        services.AddSingleton<IDiffRepository, DiffRepository>();
        services.AddSingleton<IScrapeJobRepository, ScrapeJobRepository>();
        services.AddSingleton<ILibraryProfileRepository, LibraryProfileRepository>();
        services.AddSingleton<ILibraryIndexRepository, LibraryIndexRepository>();
        services.AddSingleton<IBm25ShardRepository, Bm25ShardRepository>();

        return services;
    }

    private static void RegisterClassMaps()
    {
        if (!BsonClassMap.IsClassMapRegistered(typeof(ScrapeJobRecord)))
        {
            BsonClassMap.RegisterClassMap<ScrapeJobRecord>(cm =>
                                                           {
                                                               cm.AutoMap();
                                                               cm.SetIgnoreExtraElements(ignoreExtraElements: true);
                                                           }
                                                          );
        }

        // Bm25Stats replaced the older Bm25Index; existing documents may
        // still have the old "Postings" field at the Bm25 level. Tolerate
        // it on read so the next rescrub can repopulate cleanly.
        if (!BsonClassMap.IsClassMapRegistered(typeof(Bm25Stats)))
        {
            BsonClassMap.RegisterClassMap<Bm25Stats>(cm =>
                                                     {
                                                         cm.AutoMap();
                                                         cm.SetIgnoreExtraElements(ignoreExtraElements: true);
                                                     }
                                                    );
        }

        if (!BsonClassMap.IsClassMapRegistered(typeof(LibraryIndex)))
        {
            BsonClassMap.RegisterClassMap<LibraryIndex>(cm =>
                                                        {
                                                            cm.AutoMap();
                                                            cm.SetIgnoreExtraElements(ignoreExtraElements: true);
                                                        }
                                                       );
        }
    }
}
