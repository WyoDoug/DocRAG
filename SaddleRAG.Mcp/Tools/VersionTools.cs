// VersionTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using SaddleRAG.Database.Repositories;
using ModelContextProtocol.Server;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tools for querying version diffs between library versions.
/// </summary>
[McpServerToolType]
public static class VersionTools
{
    [McpServerTool(Name = "get_version_changes")]
    [Description("Show what changed between two versions of a library. " +
                 "Returns added, removed, and changed pages with change summaries. " +
                 "Useful for migration guidance."
                )]
    public static async Task<string> GetVersionChanges(RepositoryFactory repositoryFactory,
                                                       [Description("Library identifier")] string library,
                                                       [Description("Older version to compare from â€” defaults to previous"
                                                                   )]
                                                       string? fromVersion = null,
                                                       [Description("Newer version to compare to â€” defaults to current"
                                                                   )]
                                                       string? toVersion = null,
                                                       [Description("Optional database profile name")]
                                                       string? profile = null,
                                                       CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(library);

        var libraryRepository = repositoryFactory.GetLibraryRepository(profile);
        var diffRepository = repositoryFactory.GetDiffRepository(profile);

        var lib = await libraryRepository.GetLibraryAsync(library, ct) ??
                  throw new InvalidOperationException($"Library '{library}' not found.");

        var resolvedTo = toVersion ?? lib.CurrentVersion;
        string resolvedFrom;

        if (fromVersion != null)
            resolvedFrom = fromVersion;
        else
        {
            var idx = lib.AllVersions.IndexOf(resolvedTo);
            resolvedFrom = idx > 0
                               ? lib.AllVersions[idx - 1]
                               : throw new
                                     InvalidOperationException($"No previous version found for '{resolvedTo}'. Specify fromVersion explicitly."
                                                              );
        }

        var diff = await diffRepository.GetDiffAsync(library, resolvedFrom, resolvedTo, ct);

        string json;
        if (diff == null)
            json = $"No diff record found for {library} {resolvedFrom} â†’ {resolvedTo}.";
        else
            json = JsonSerializer.Serialize(diff, new JsonSerializerOptions { WriteIndented = true });

        return json;
    }
}
