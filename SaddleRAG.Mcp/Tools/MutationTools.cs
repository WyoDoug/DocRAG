// MutationTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using ModelContextProtocol.Server;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tools that mutate library state — rename, delete library,
///     delete version. All default to dryRun=true so the calling LLM
///     can preview the cascade before committing.
/// </summary>
[McpServerToolType]
public static class MutationTools
{
    private const string PreservedForAudit = "preserved for audit";
    private const string NotFoundStatus = "NotFound";

    [McpServerTool(Name = "rename_library")]
    [Description("Rename a library across every collection that stores LibraryId. " +
                 "Defaults to dryRun=true — preview the per-collection update counts " +
                 "before passing dryRun=false to apply. Errors with Outcome=Collision " +
                 "if the new id already exists; never silently merges libraries."
                )]
    public static async Task<string> RenameLibrary(RepositoryFactory repositoryFactory,
                                                   [Description("Current library identifier")]
                                                   string library,
                                                   [Description("New library identifier")]
                                                   string newId,
                                                   [Description("If true (default), preview without writing.")]
                                                   bool dryRun = true,
                                                   [Description("Optional database profile name")]
                                                   string? profile = null,
                                                   CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(newId);

        var libraryRepo = repositoryFactory.GetLibraryRepository(profile);

        string result;
        if (dryRun)
        {
            var preview = await PreviewRenameAsync(repositoryFactory, profile, library, newId, ct);
            var response = new
                               {
                                   DryRun = true,
                                   Outcome = preview.outcome.ToString(),
                                   WouldRename = preview.counts
                               };
            result = JsonSerializer.Serialize(response, smJsonOptions);
        }
        else
        {
            var renameResult = await libraryRepo.RenameAsync(library, newId, ct);
            var response = new
                               {
                                   DryRun = false,
                                   Outcome = renameResult.Outcome.ToString(),
                                   Counts = renameResult.Counts
                               };
            result = JsonSerializer.Serialize(response, smJsonOptions);
        }

        return result;
    }

    private static async Task<(RenameLibraryOutcome outcome, RenameLibraryResult? counts)> PreviewRenameAsync(
        RepositoryFactory factory, string? profile, string oldId, string newId, CancellationToken ct)
    {
        var libraryRepo = factory.GetLibraryRepository(profile);
        var existing = await libraryRepo.GetLibraryAsync(oldId, ct);
        var collision = await libraryRepo.GetLibraryAsync(newId, ct);

        (RenameLibraryOutcome outcome, RenameLibraryResult? counts) result = existing switch
        {
            null => (RenameLibraryOutcome.NotFound, null),
            _ when collision != null => (RenameLibraryOutcome.Collision, null),
            _ => MakeSuccessResult(existing)
        };

        return result;
    }

    private static (RenameLibraryOutcome outcome, RenameLibraryResult counts) MakeSuccessResult(
        LibraryRecord existing) =>
    (
        RenameLibraryOutcome.Renamed,
        new RenameLibraryResult(
            Libraries: 1,
            Versions: existing.AllVersions.Count,
            Chunks: 0,
            Pages: 0,
            Profiles: 0,
            Indexes: 0,
            Bm25Shards: 0,
            ExcludedSymbols: 0,
            ScrapeJobs: 0)
    );

    [McpServerTool(Name = "delete_version")]
    [Description("Hard-delete one (library, version): chunks, pages, profile, indexes, " +
                 "bm25 shards, excluded symbols, and the LibraryVersions row. Cascade-deletes " +
                 "the parent Library row if no other versions remain. ScrapeJobs are retained " +
                 "for audit. Defaults to dryRun=true."
                )]
    public static async Task<string> DeleteVersion(RepositoryFactory repositoryFactory,
                                                   [Description("Library identifier")]
                                                   string library,
                                                   [Description("Version to delete")]
                                                   string version,
                                                   [Description("If true (default), preview without writing.")]
                                                   bool dryRun = true,
                                                   [Description("Optional database profile name")]
                                                   string? profile = null,
                                                   CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var chunkRepo = repositoryFactory.GetChunkRepository(profile);
        var pageRepo = repositoryFactory.GetPageRepository(profile);
        var profileRepo = repositoryFactory.GetLibraryProfileRepository(profile);
        var indexRepo = repositoryFactory.GetLibraryIndexRepository(profile);
        var bm25Repo = repositoryFactory.GetBm25ShardRepository(profile);
        var excludedRepo = repositoryFactory.GetExcludedSymbolsRepository(profile);
        var libraryRepo = repositoryFactory.GetLibraryRepository(profile);

        string result;
        if (dryRun)
        {
            var chunks = await chunkRepo.GetChunkCountAsync(library, version, ct);
            var pages = await pageRepo.GetPageCountAsync(library, version, ct);
            var lib = await libraryRepo.GetLibraryAsync(library, ct);
            bool wouldDeleteLibraryRow = lib != null && lib.AllVersions.Count == 1 && lib.AllVersions[0] == version;
            string? wouldRepointTo = lib != null && lib.CurrentVersion == version && lib.AllVersions.Count > 1
                ? lib.AllVersions.First(v => v != version)
                : null;

            var preview = new
                              {
                                  DryRun = true,
                                  WouldDelete = new
                                                    {
                                                        Versions = new[] { version },
                                                        Chunks = chunks,
                                                        Pages = pages,
                                                        Profiles = 1,
                                                        Indexes = 1,
                                                        Bm25Shards = 1,
                                                        ExcludedSymbols = 1
                                                    },
                                  LibraryRowAffected = wouldDeleteLibraryRow,
                                  CurrentVersionRepointedTo = wouldRepointTo,
                                  ScrapeJobsRetained = PreservedForAudit
                              };
            result = JsonSerializer.Serialize(preview, smJsonOptions);
        }
        else
        {
            var chunks = await chunkRepo.DeleteChunksAsync(library, version, ct);
            var pages = await pageRepo.DeleteAsync(library, version, ct);
            var profiles = await profileRepo.DeleteAsync(library, version, ct);
            var indexes = await indexRepo.DeleteAsync(library, version, ct);
            var shards = await bm25Repo.DeleteAsync(library, version, ct);
            var excluded = await excludedRepo.DeleteAsync(library, version, ct);
            var versionResult = await libraryRepo.DeleteVersionAsync(library, version, ct);

            var response = new
                               {
                                   DryRun = false,
                                   Deleted = new
                                                 {
                                                     Versions = versionResult.VersionsDeleted,
                                                     Chunks = chunks,
                                                     Pages = pages,
                                                     Profiles = profiles,
                                                     Indexes = indexes,
                                                     Bm25Shards = shards,
                                                     ExcludedSymbols = excluded
                                                 },
                                   versionResult.LibraryRowDeleted,
                                   versionResult.CurrentVersionRepointedTo
                               };
            result = JsonSerializer.Serialize(response, smJsonOptions);
        }

        return result;
    }

    [McpServerTool(Name = "delete_library")]
    [Description("Hard-delete an entire library across every collection except ScrapeJobs " +
                 "(retained for audit). Cascades through every version. Defaults to " +
                 "dryRun=true so the calling LLM can preview the cascade before applying."
                )]
    public static async Task<string> DeleteLibrary(RepositoryFactory repositoryFactory,
                                                   [Description("Library identifier")]
                                                   string library,
                                                   [Description("If true (default), preview without writing.")]
                                                   bool dryRun = true,
                                                   [Description("Optional database profile name")]
                                                   string? profile = null,
                                                   CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(library);

        var libraryRepo = repositoryFactory.GetLibraryRepository(profile);
        var chunkRepo = repositoryFactory.GetChunkRepository(profile);
        var pageRepo = repositoryFactory.GetPageRepository(profile);
        var profileRepo = repositoryFactory.GetLibraryProfileRepository(profile);
        var indexRepo = repositoryFactory.GetLibraryIndexRepository(profile);
        var bm25Repo = repositoryFactory.GetBm25ShardRepository(profile);
        var excludedRepo = repositoryFactory.GetExcludedSymbolsRepository(profile);

        var lib = await libraryRepo.GetLibraryAsync(library, ct);
        string result = lib switch
        {
            null => JsonSerializer.Serialize(new { Status = NotFoundStatus, Library = library }, smJsonOptions),
            _ when dryRun => await GetDeleteLibraryDryRunResultAsync(library, lib, chunkRepo, pageRepo, ct),
            _ => await GetDeleteLibraryApplyResultAsync(library, lib, chunkRepo, pageRepo, profileRepo, indexRepo, bm25Repo, excludedRepo, libraryRepo, ct)
        };

        return result;
    }

    private static async Task<string> GetDeleteLibraryDryRunResultAsync(
        string library, LibraryRecord lib, IChunkRepository chunkRepo, IPageRepository pageRepo, CancellationToken ct)
    {
        long totalChunks = 0;
        long totalPages = 0;
        foreach (var v in lib.AllVersions)
        {
            totalChunks += await chunkRepo.GetChunkCountAsync(library, v, ct);
            totalPages += await pageRepo.GetPageCountAsync(library, v, ct);
        }

        var preview = new
                          {
                              DryRun = true,
                              WouldDelete = new
                                                {
                                                    Library = 1,
                                                    Versions = lib.AllVersions.ToArray(),
                                                    Chunks = totalChunks,
                                                    Pages = totalPages,
                                                    Profiles = lib.AllVersions.Count,
                                                    Indexes = lib.AllVersions.Count,
                                                    Bm25Shards = lib.AllVersions.Count,
                                                    ExcludedSymbols = lib.AllVersions.Count
                                                },
                              ScrapeJobsRetained = PreservedForAudit
                          };
        return JsonSerializer.Serialize(preview, smJsonOptions);
    }

    private static async Task<string> GetDeleteLibraryApplyResultAsync(
        string library,
        LibraryRecord lib,
        IChunkRepository chunkRepo,
        IPageRepository pageRepo,
        ILibraryProfileRepository profileRepo,
        ILibraryIndexRepository indexRepo,
        IBm25ShardRepository bm25Repo,
        IExcludedSymbolsRepository excludedRepo,
        ILibraryRepository libraryRepo,
        CancellationToken ct)
    {
        long chunks = 0, pages = 0, profiles = 0, indexes = 0, shards = 0, excluded = 0;
        foreach (var v in lib.AllVersions)
        {
            chunks += await chunkRepo.DeleteChunksAsync(library, v, ct);
            pages += await pageRepo.DeleteAsync(library, v, ct);
            profiles += await profileRepo.DeleteAsync(library, v, ct);
            indexes += await indexRepo.DeleteAsync(library, v, ct);
            shards += await bm25Repo.DeleteAsync(library, v, ct);
            excluded += await excludedRepo.DeleteAsync(library, v, ct);
        }

        var versionsDeleted = await libraryRepo.DeleteAsync(library, ct);

        var response = new
                           {
                               DryRun = false,
                               Deleted = new
                                             {
                                                 Library = 1,
                                                 Versions = versionsDeleted,
                                                 Chunks = chunks,
                                                 Pages = pages,
                                                 Profiles = profiles,
                                                 Indexes = indexes,
                                                 Bm25Shards = shards,
                                                 ExcludedSymbols = excluded
                                             }
                           };
        return JsonSerializer.Serialize(response, smJsonOptions);
    }

    private static readonly JsonSerializerOptions smJsonOptions = new() { WriteIndented = true };
}
