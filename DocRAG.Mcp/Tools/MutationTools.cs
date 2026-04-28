// MutationTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using DocRAG.Core.Models;
using DocRAG.Database.Repositories;
using ModelContextProtocol.Server;

#endregion

namespace DocRAG.Mcp.Tools;

/// <summary>
///     MCP tools that mutate library state — rename, delete library,
///     delete version. All default to dryRun=true so the calling LLM
///     can preview the cascade before committing.
/// </summary>
[McpServerToolType]
public static class MutationTools
{
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

    private static readonly JsonSerializerOptions smJsonOptions = new() { WriteIndented = true };
}
