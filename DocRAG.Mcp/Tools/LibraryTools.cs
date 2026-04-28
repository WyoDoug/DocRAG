// LibraryTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;
using DocRAG.Database.Repositories;
using ModelContextProtocol.Server;

#endregion

namespace DocRAG.Mcp.Tools;

/// <summary>
///     MCP tools for listing and querying available documentation libraries.
/// </summary>
[McpServerToolType]
public static class LibraryTools
{
    private const string KindClass = "class";
    private const string KindEnum = "enum";
    private const string KindFunction = "function";
    private const string KindParameter = "parameter";

    [McpServerTool(Name = "list_libraries")]
    [Description("List all available documentation libraries with their current version and all ingested versions.")]
    public static async Task<string> ListLibraries(RepositoryFactory repositoryFactory,
                                                   [Description("Optional database profile name (use list_profiles to discover)"
                                                               )]
                                                   string? profile = null,
                                                   CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);

        var libraryRepository = repositoryFactory.GetLibraryRepository(profile);
        var libraries = await libraryRepository.GetAllLibrariesAsync(ct);

        string result;
        if (libraries.Count == 0)
        {
            var emptyResponse = new
                                    {
                                        Libraries = Array.Empty<object>(),
                                        Hint = EmptyDatabaseHint
                                    };
            result = JsonSerializer.Serialize(emptyResponse, smJsonOptions);
        }
        else
            result = JsonSerializer.Serialize(libraries, smJsonOptions);

        return result;
    }

    private const string EmptyDatabaseHint = "Database is empty. Call get_dashboard_index for orientation, or use index_project_dependencies(path=...) / scrape_docs(url=..., libraryId=..., version=...) to ingest.";

    [McpServerTool(Name = "list_symbols")]
    [Description("List documented symbols for a library, optionally filtered by kind. " +
                 "kind=class|enum|function|parameter, or omit for all kinds. " +
                 "Returns [{name, kind}] so callers can render heterogeneous results."
                )]
    public static async Task<string> ListSymbols(RepositoryFactory repositoryFactory,
                                                 [Description("Library identifier")]
                                                 string library,
                                                 [Description("Symbol kind filter: 'class', 'enum', 'function', 'parameter', or null for all")]
                                                 string? kind = null,
                                                 [Description("Optional partial name filter (case-insensitive)")]
                                                 string? filter = null,
                                                 [Description("Specific version — defaults to current")]
                                                 string? version = null,
                                                 [Description("Optional database profile name")]
                                                 string? profile = null,
                                                 CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(library);

        var libraryRepo = repositoryFactory.GetLibraryRepository(profile);
        var chunkRepo = repositoryFactory.GetChunkRepository(profile);
        var resolvedVersion = await ResolveVersionAsync(libraryRepo, library, version, ct);

        string result;
        if (resolvedVersion == null)
        {
            var nf = new { Error = $"Library '{library}' not found." };
            result = JsonSerializer.Serialize(nf, smJsonOptions);
        }
        else
        {
            var entries = new List<object>();
            if (kind == null)
            {
                var all = await chunkRepo.GetAllSymbolsAsync(library, resolvedVersion, filter, ct);
                foreach (var s in all)
                    entries.Add(new { name = s.Name, kind = KindToString(s.Kind) });
            }
            else
            {
                var parsed = ParseKind(kind);
                var names = await chunkRepo.GetSymbolsAsync(library, resolvedVersion, parsed, filter, ct);
                var kindString = kind.ToLowerInvariant();
                foreach (var n in names)
                    entries.Add(new { name = n, kind = kindString });
            }
            result = JsonSerializer.Serialize(entries, smJsonOptions);
        }
        return result;
    }

    private static SymbolKind ParseKind(string raw) => raw.ToLowerInvariant() switch
    {
        KindClass => SymbolKind.Type,
        KindEnum => SymbolKind.Enum,
        KindFunction => SymbolKind.Function,
        KindParameter => SymbolKind.Parameter,
        _ => throw new ArgumentException($"Unknown kind '{raw}'. Expected: class, enum, function, parameter.")
    };

    private static string KindToString(SymbolKind kind) => kind switch
    {
        SymbolKind.Type => KindClass,
        SymbolKind.Enum => KindEnum,
        SymbolKind.Function => KindFunction,
        SymbolKind.Parameter => KindParameter,
        _ => kind.ToString().ToLowerInvariant()
    };

    internal static async Task<string?> ResolveVersionAsync(ILibraryRepository libraryRepository,
                                                            string libraryId,
                                                            string? version,
                                                            CancellationToken ct)
    {
        string? result;

        if (!string.IsNullOrEmpty(version))
            result = version;
        else
        {
            var library = await libraryRepository.GetLibraryAsync(libraryId, ct);
            result = library?.CurrentVersion;
        }

        return result;
    }

    private static readonly JsonSerializerOptions smJsonOptions = new() { WriteIndented = true };
}
