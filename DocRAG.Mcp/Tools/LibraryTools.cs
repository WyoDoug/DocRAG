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
        var result = JsonSerializer.Serialize(libraries, smJsonOptions);
        return result;
    }

    [McpServerTool(Name = "list_classes")]
    [Description("List all documented classes/types for a library. Useful for discovering what API reference is available."
                )]
    public static async Task<string> ListClasses(RepositoryFactory repositoryFactory,
                                                 [Description("Library identifier (e.g. 'infragistics-wpf', 'questpdf')"
                                                             )]
                                                 string library,
                                                 [Description("Optional partial name filter")]
                                                 string? filter = null,
                                                 [Description("Specific version — defaults to current")]
                                                 string? version = null,
                                                 [Description("Optional database profile name")]
                                                 string? profile = null,
                                                 CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(library);

        var libraryRepository = repositoryFactory.GetLibraryRepository(profile);
        var chunkRepository = repositoryFactory.GetChunkRepository(profile);

        var resolvedVersion = await ResolveVersionAsync(libraryRepository, library, version, ct);
        var result = await ProjectNamesOrNotFoundAsync(library,
                                                       resolvedVersion,
                                                       filter,
                                                       (lib, ver, f, c) => chunkRepository.GetQualifiedNamesAsync(lib, ver, f, c),
                                                       ct
                                                      );
        return result;
    }

    [McpServerTool(Name = "list_enums")]
    [Description("List documented enum types for a library. Returns only Symbols with Kind == Enum from " +
                 "the identifier-aware extractor; complements list_classes by surfacing enumerations " +
                 "that are not classes."
                )]
    public static async Task<string> ListEnums(RepositoryFactory repositoryFactory,
                                               [Description("Library identifier")]
                                               string library,
                                               [Description("Optional partial name filter")]
                                               string? filter = null,
                                               [Description("Specific version — defaults to current")]
                                               string? version = null,
                                               [Description("Optional database profile name")]
                                               string? profile = null,
                                               CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(library);

        var result = await ListSymbolsByKindAsync(repositoryFactory, library, SymbolKind.Enum, filter, version, profile, ct);
        return result;
    }

    [McpServerTool(Name = "list_functions")]
    [Description("List documented functions/methods for a library. Returns only Symbols with " +
                 "Kind == Function from the identifier-aware extractor."
                )]
    public static async Task<string> ListFunctions(RepositoryFactory repositoryFactory,
                                                   [Description("Library identifier")]
                                                   string library,
                                                   [Description("Optional partial name filter")]
                                                   string? filter = null,
                                                   [Description("Specific version — defaults to current")]
                                                   string? version = null,
                                                   [Description("Optional database profile name")]
                                                   string? profile = null,
                                                   CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(library);

        var result = await ListSymbolsByKindAsync(repositoryFactory, library, SymbolKind.Function, filter, version, profile, ct);
        return result;
    }

    [McpServerTool(Name = "list_parameters")]
    [Description("List documented configurable parameters for a library. Useful for hardware/motion-control " +
                 "docs (Aerotech etc.) where the primary unit of API surface is a parameter rather than a class."
                )]
    public static async Task<string> ListParameters(RepositoryFactory repositoryFactory,
                                                    [Description("Library identifier")]
                                                    string library,
                                                    [Description("Optional partial name filter")]
                                                    string? filter = null,
                                                    [Description("Specific version — defaults to current")]
                                                    string? version = null,
                                                    [Description("Optional database profile name")]
                                                    string? profile = null,
                                                    CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(library);

        var result = await ListSymbolsByKindAsync(repositoryFactory, library, SymbolKind.Parameter, filter, version, profile, ct);
        return result;
    }

    private static async Task<string> ListSymbolsByKindAsync(RepositoryFactory repositoryFactory,
                                                             string library,
                                                             SymbolKind kind,
                                                             string? filter,
                                                             string? version,
                                                             string? profile,
                                                             CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(library);

        var libraryRepository = repositoryFactory.GetLibraryRepository(profile);
        var chunkRepository = repositoryFactory.GetChunkRepository(profile);

        var resolvedVersion = await ResolveVersionAsync(libraryRepository, library, version, ct);
        var result = await ProjectNamesOrNotFoundAsync(library,
                                                       resolvedVersion,
                                                       filter,
                                                       (lib, ver, f, c) => chunkRepository.GetSymbolsAsync(lib, ver, kind, f, c),
                                                       ct
                                                      );
        return result;
    }

    private static async Task<string> ProjectNamesOrNotFoundAsync(string library,
                                                                  string? resolvedVersion,
                                                                  string? filter,
                                                                  Func<string, string, string?, CancellationToken, Task<IReadOnlyList<string>>> fetch,
                                                                  CancellationToken ct)
    {
        string result;
        if (resolvedVersion == null)
        {
            var notFound = new { Error = $"Library '{library}' not found." };
            result = JsonSerializer.Serialize(notFound, smJsonOptions);
        }
        else
        {
            var names = await fetch(library, resolvedVersion, filter, ct);
            result = JsonSerializer.Serialize(names, smJsonOptions);
        }

        return result;
    }

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
