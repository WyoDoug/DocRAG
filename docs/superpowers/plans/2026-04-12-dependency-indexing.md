# Dependency Indexing & Simplified Scraping — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Auto-discover project dependencies (NuGet/npm/pip), resolve doc URLs, scrape and cache locally, with a simplified `scrape_docs` MCP tool for ad-hoc use.

**Architecture:** Three interfaces per concern (`IProjectFileParser`, `IPackageRegistryClient`, `IDocUrlResolver`) with ecosystem implementations. `DependencyIndexer` orchestrator ties scan→resolve→scrape into one pipeline. `ScrapeJobFactory` auto-derives crawl config from a URL. 3-tier crawl depth model replaces the single `OutOfScopeMaxDepth`.

**Tech Stack:** C# / .NET 10, MongoDB, Playwright, Ollama, OllamaSharp, ModelContextProtocol.AspNetCore, System.Text.Json, IHttpClientFactory

**Spec:** `docs/superpowers/specs/2026-04-12-dependency-indexing-design.md`

**Coding Standards:** CodeStructure.Analyzers enforced. Single return (variable pattern), no `continue`, no if/else chains, `m` field prefix, Allman braces, no magic numbers, no inline comments.

---

## Task 1: Core Interfaces and Models

**Files:**
- Create: `SaddleRAG.Core/Interfaces/IProjectFileParser.cs`
- Create: `SaddleRAG.Core/Interfaces/IPackageRegistryClient.cs`
- Create: `SaddleRAG.Core/Interfaces/IDocUrlResolver.cs`
- Create: `SaddleRAG.Core/Models/PackageDependency.cs`
- Create: `SaddleRAG.Core/Models/PackageMetadata.cs`
- Create: `SaddleRAG.Core/Models/DocUrlResolution.cs`
- Create: `SaddleRAG.Core/Models/DependencyIndexReport.cs`

- [ ] **Step 1: Create PackageDependency model**

```csharp
// PackageDependency.cs
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.

namespace SaddleRAG.Core.Models;

/// <summary>
///     A single package dependency discovered from a project file.
/// </summary>
public record PackageDependency
{
    /// <summary>
    ///     Package identifier (e.g. "Newtonsoft.Json", "express", "requests").
    /// </summary>
    public required string PackageId { get; init; }

    /// <summary>
    ///     Version string as declared in the project file.
    ///     May be a range for npm/pip; resolved to exact version during registry lookup.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    ///     Ecosystem this package belongs to ("nuget", "npm", "pip").
    /// </summary>
    public required string EcosystemId { get; init; }
}
```

- [ ] **Step 2: Create PackageMetadata model**

```csharp
// PackageMetadata.cs
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Metadata fetched from a package registry for documentation resolution.
/// </summary>
public record PackageMetadata
{
    /// <summary>
    ///     Package identifier.
    /// </summary>
    public required string PackageId { get; init; }

    /// <summary>
    ///     Resolved version string.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    ///     Ecosystem identifier.
    /// </summary>
    public required string EcosystemId { get; init; }

    /// <summary>
    ///     Project home page URL from registry metadata.
    /// </summary>
    public string ProjectUrl { get; init; } = string.Empty;

    /// <summary>
    ///     Source repository URL (often GitHub).
    /// </summary>
    public string RepositoryUrl { get; init; } = string.Empty;

    /// <summary>
    ///     Explicit documentation URL if the registry provides one.
    /// </summary>
    public string DocumentationUrl { get; init; } = string.Empty;

    /// <summary>
    ///     Package description from registry metadata.
    /// </summary>
    public string Description { get; init; } = string.Empty;
}
```

- [ ] **Step 3: Create DocUrlResolution model**

```csharp
// DocUrlResolution.cs
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.

using SaddleRAG.Core.Enums;

namespace SaddleRAG.Core.Models;

/// <summary>
///     Result of resolving a package to its documentation URL.
/// </summary>
public record DocUrlResolution
{
    /// <summary>
    ///     The resolved documentation URL to scrape. Null if resolution failed.
    /// </summary>
    public string? DocUrl { get; init; }

    /// <summary>
    ///     How the URL was discovered: "registry", "pattern", "github-repo", "none".
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    ///     Confidence in the resolved URL being useful documentation.
    /// </summary>
    public required ScanConfidence Confidence { get; init; }
}
```

- [ ] **Step 4: Create DependencyIndexReport model**

```csharp
// DependencyIndexReport.cs
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Report returned by the dependency indexing pipeline.
/// </summary>
public record DependencyIndexReport
{
    /// <summary>
    ///     Path that was scanned.
    /// </summary>
    public required string ProjectPath { get; init; }

    /// <summary>
    ///     Total package dependencies found across all ecosystems.
    /// </summary>
    public required int TotalDependencies { get; init; }

    /// <summary>
    ///     Packages filtered out (framework, tooling, test).
    /// </summary>
    public required int FilteredOut { get; init; }

    /// <summary>
    ///     Packages already cached at the exact version.
    /// </summary>
    public required int AlreadyCached { get; init; }

    /// <summary>
    ///     Packages cached but at a different version.
    /// </summary>
    public required int CachedDifferentVersion { get; init; }

    /// <summary>
    ///     Packages queued for scraping.
    /// </summary>
    public required int NewlyQueued { get; init; }

    /// <summary>
    ///     Packages where doc URL resolution failed.
    /// </summary>
    public required int ResolutionFailed { get; init; }

    /// <summary>
    ///     Per-package status details.
    /// </summary>
    public required IReadOnlyList<PackageIndexStatus> Packages { get; init; }
}

/// <summary>
///     Status of a single package in the dependency indexing pipeline.
/// </summary>
public record PackageIndexStatus
{
    /// <summary>
    ///     Package identifier.
    /// </summary>
    public required string PackageId { get; init; }

    /// <summary>
    ///     Version from the project file.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    ///     Ecosystem ("nuget", "npm", "pip").
    /// </summary>
    public required string EcosystemId { get; init; }

    /// <summary>
    ///     Pipeline status: "Cached", "CachedDifferentVersion", "Queued",
    ///     "NoDocumentationFound", "ResolutionFailed".
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    ///     Resolved documentation URL, if found.
    /// </summary>
    public string? DocUrl { get; init; }

    /// <summary>
    ///     Version currently cached, if any.
    /// </summary>
    public string? CachedVersion { get; init; }

    /// <summary>
    ///     Error message if resolution or registry lookup failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    ///     Scrape job ID if queued.
    /// </summary>
    public string? JobId { get; init; }
}
```

- [ ] **Step 5: Create the three core interfaces**

`IProjectFileParser.cs`:
```csharp
// IProjectFileParser.cs
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.

using SaddleRAG.Core.Models;

namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     Parses a project file to discover package dependencies for a specific ecosystem.
/// </summary>
public interface IProjectFileParser
{
    /// <summary>
    ///     Ecosystem identifier (e.g. "nuget", "npm", "pip").
    /// </summary>
    string EcosystemId { get; }

    /// <summary>
    ///     File patterns this parser handles (e.g. "*.csproj", "package.json").
    /// </summary>
    IReadOnlyList<string> FilePatterns { get; }

    /// <summary>
    ///     Parse the given project file and return discovered dependencies.
    /// </summary>
    Task<IReadOnlyList<PackageDependency>> ParseAsync(string filePath, CancellationToken ct = default);
}
```

`IPackageRegistryClient.cs`:
```csharp
// IPackageRegistryClient.cs
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.

using SaddleRAG.Core.Models;

namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     Fetches package metadata from an ecosystem's package registry.
/// </summary>
public interface IPackageRegistryClient
{
    /// <summary>
    ///     Ecosystem identifier (e.g. "nuget", "npm", "pip").
    /// </summary>
    string EcosystemId { get; }

    /// <summary>
    ///     Fetch metadata for a specific package version.
    ///     Returns null if the package is not found or the registry is unreachable.
    /// </summary>
    Task<PackageMetadata?> FetchMetadataAsync(string packageId, string version, CancellationToken ct = default);
}
```

`IDocUrlResolver.cs`:
```csharp
// IDocUrlResolver.cs
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.

using SaddleRAG.Core.Models;

namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     Resolves package metadata into a scrapable documentation URL.
/// </summary>
public interface IDocUrlResolver
{
    /// <summary>
    ///     Ecosystem identifier (e.g. "nuget", "npm", "pip").
    /// </summary>
    string EcosystemId { get; }

    /// <summary>
    ///     Resolve a documentation URL from package metadata.
    /// </summary>
    Task<DocUrlResolution> ResolveAsync(PackageMetadata metadata, CancellationToken ct = default);
}
```

- [ ] **Step 6: Build SaddleRAG.Core and verify**

Run: `dotnet build E:/Projects/RAG/SaddleRAG.Core/SaddleRAG.Core.csproj`
Expected: Build succeeded, 0 warnings, 0 errors

- [ ] **Step 7: Commit**

```
git add SaddleRAG.Core/Interfaces/IProjectFileParser.cs SaddleRAG.Core/Interfaces/IPackageRegistryClient.cs SaddleRAG.Core/Interfaces/IDocUrlResolver.cs SaddleRAG.Core/Models/PackageDependency.cs SaddleRAG.Core/Models/PackageMetadata.cs SaddleRAG.Core/Models/DocUrlResolution.cs SaddleRAG.Core/Models/DependencyIndexReport.cs
```
Message: `Add core interfaces and models for dependency indexing pipeline`

---

## Task 2: 3-Tier Crawl Depth Model

**Files:**
- Modify: `SaddleRAG.Core/Models/ScrapeJob.cs`
- Modify: `SaddleRAG.Ingestion/Crawling/PageCrawler.cs`

- [ ] **Step 1: Update ScrapeJob with new depth fields**

In `SaddleRAG.Core/Models/ScrapeJob.cs`, replace `OutOfScopeMaxDepth` with two new fields:

```csharp
/// <summary>
///     Maximum link-following depth for pages on the SAME HOST
///     but outside the root path prefix.
///     Pages within the root scope are crawled with unlimited depth.
/// </summary>
public int SameHostDepth { get; init; } = 5;

/// <summary>
///     Maximum link-following depth for pages on a DIFFERENT HOST entirely.
///     Set to 0 to disable off-site crawling.
/// </summary>
public int OffSiteDepth { get; init; } = 1;
```

Remove the `OutOfScopeMaxDepth` property.

- [ ] **Step 2: Update PageCrawler depth logic**

In `PageCrawler.cs`, update the `CrawlEntry` record to track host-awareness:

```csharp
private record CrawlEntry(string Url, int SameHostDepth, int OffSiteDepth);
```

Update the depth assignment logic in both `DryRunAsync` and `CrawlAsync`. Where the code currently checks `IsInRootScope` and assigns a single depth, add a third check:

```csharp
bool inRootScope = IsInRootScope(normalized, rootScope);
bool sameHost = IsSameHost(normalized, rootScope);

int childSameHostDepth;
int childOffSiteDepth;
if (inRootScope)
{
    childSameHostDepth = 0;
    childOffSiteDepth = 0;
}
else if (sameHost)
{
    childSameHostDepth = entry.SameHostDepth + 1;
    childOffSiteDepth = entry.OffSiteDepth;
}
else
{
    childSameHostDepth = entry.SameHostDepth;
    childOffSiteDepth = entry.OffSiteDepth + 1;
}
```

Add the depth limit check:
```csharp
bool depthExceeded = false;
if (!inRootScope && sameHost)
{
    depthExceeded = entry.SameHostDepth >= job.SameHostDepth;
}
else if (!inRootScope && !sameHost)
{
    depthExceeded = entry.OffSiteDepth >= job.OffSiteDepth;
}
```

Add `IsSameHost` helper:
```csharp
private static bool IsSameHost(string url, RootScope scope)
{
    bool result = false;
    try
    {
        var uri = new Uri(url);
        result = string.Equals(uri.Host, scope.Host, StringComparison.OrdinalIgnoreCase);
    }
    catch
    {
        // Malformed URL
    }
    return result;
}
```

- [ ] **Step 3: Update all callers of OutOfScopeMaxDepth**

Grep for `OutOfScopeMaxDepth` across the codebase and update:
- `SaddleRAG.Mcp/Tools/IngestionTools.cs` — the `dryrun_scrape` and `scrape_library` tools reference this field. Update parameter names and mapping.
- `SaddleRAG.Cli/Program.cs` — if it references `OutOfScopeMaxDepth`, update.

- [ ] **Step 4: Build and verify**

Run: `dotnet build E:/Projects/RAG/SaddleRAG.slnx`
Expected: Build succeeded, 0 warnings, 0 errors

- [ ] **Step 5: Commit**

Message: `Replace OutOfScopeMaxDepth with 3-tier depth model (SameHostDepth + OffSiteDepth)`

---

## Task 3: ScrapeJobFactory and scrape_docs Tool

**Files:**
- Create: `SaddleRAG.Ingestion/Scanning/ScrapeJobFactory.cs`
- Create: `SaddleRAG.Mcp/Tools/ScrapeDocsTools.cs`

- [ ] **Step 1: Create ScrapeJobFactory**

```csharp
// ScrapeJobFactory.cs
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.

using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Crawling;

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>
///     Creates ScrapeJob instances from a URL with sensible auto-derived defaults.
///     Used by scrape_docs and the dependency indexing pipeline.
/// </summary>
public static class ScrapeJobFactory
{
    private const int DefaultMaxPages = 500;
    private const int DefaultFetchDelayMs = 500;
    private const int DefaultSameHostDepth = 5;
    private const int DefaultOffSiteDepth = 1;

    private static readonly string[] smDefaultExcludedPatterns =
    [
        @"/blog/", @"/pricing/", @"/login/", @"/search",
        @"/account/", @"/cart/", @"mailto:", @"#"
    ];

    /// <summary>
    ///     Create a ScrapeJob from a URL with auto-derived scope and defaults.
    /// </summary>
    public static ScrapeJob CreateFromUrl(
        string url,
        string libraryId,
        string version,
        string? hint = null,
        int maxPages = DefaultMaxPages,
        int fetchDelayMs = DefaultFetchDelayMs)
    {
        var uri = new Uri(url);

        var job = new ScrapeJob
        {
            RootUrl = url,
            LibraryId = libraryId,
            Version = version,
            LibraryHint = hint ?? libraryId,
            AllowedUrlPatterns = [uri.Host],
            ExcludedUrlPatterns = smDefaultExcludedPatterns,
            MaxPages = maxPages,
            FetchDelayMs = fetchDelayMs,
            SameHostDepth = DefaultSameHostDepth,
            OffSiteDepth = DefaultOffSiteDepth
        };
        return job;
    }
}
```

- [ ] **Step 2: Create ScrapeDocsTools with scrape_docs and index_project_dependencies**

```csharp
// ScrapeDocsTools.cs
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.

using System.ComponentModel;
using System.Text.Json;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion;
using SaddleRAG.Ingestion.Scanning;
using ModelContextProtocol.Server;

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tools for simplified documentation scraping and dependency indexing.
/// </summary>
[McpServerToolType]
public static class ScrapeDocsTools
{
    [McpServerTool(Name = "scrape_docs")]
    [Description(
        "Scrape documentation from a URL with auto-derived crawl settings. " +
        "Just provide the URL and a library identifier — the system figures out " +
        "scope, depth limits, and exclusion patterns automatically. " +
        "Use this for ad-hoc documentation sites, vendor SDKs, or any URL " +
        "that isn't a package manager dependency. " +
        "Checks cache first — won't re-scrape if already indexed unless force=true.")]
    public static async Task<string> ScrapeDocs(
        ScrapeJobRunner runner,
        RepositoryFactory repositoryFactory,
        [Description("Root URL of the documentation site")] string url,
        [Description("Unique library identifier for cache key")] string libraryId,
        [Description("Version string for cache key")] string version,
        [Description("Human-readable hint about what this library is")] string? hint = null,
        [Description("Maximum pages to crawl (default 500)")] int maxPages = 500,
        [Description("Delay between fetches in ms (default 500)")] int fetchDelayMs = 500,
        [Description("Re-scrape even if already cached")] bool force = false,
        [Description("Optional database profile name")] string? profile = null,
        CancellationToken ct = default)
    {
        var libraryRepo = repositoryFactory.GetLibraryRepository(profile);

        // Cache check
        string json;
        var existingVersion = await libraryRepo.GetVersionAsync(libraryId, version, ct);
        if (existingVersion != null && !force)
        {
            var cached = new
            {
                Status = "AlreadyCached",
                LibraryId = libraryId,
                Version = version,
                Message = $"Documentation for {libraryId} v{version} is already indexed " +
                          $"({existingVersion.ChunkCount} chunks). Use force=true to re-scrape."
            };
            json = JsonSerializer.Serialize(cached, new JsonSerializerOptions { WriteIndented = true });
        }
        else
        {
            var job = ScrapeJobFactory.CreateFromUrl(url, libraryId, version, hint, maxPages, fetchDelayMs);
            var jobId = await runner.QueueAsync(job, profile, ct);

            var response = new
            {
                JobId = jobId,
                Status = "Queued",
                LibraryId = libraryId,
                Version = version,
                Message = $"Scrape job queued. Poll get_scrape_status with jobId='{jobId}' for progress."
            };
            json = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        return json;
    }

    [McpServerTool(Name = "index_project_dependencies")]
    [Description(
        "Scan a project to discover all package dependencies (NuGet, npm, pip), " +
        "resolve their documentation URLs, and scrape everything not already cached. " +
        "Pass a directory path to auto-detect project files, or a specific " +
        ".sln/.csproj/package.json/requirements.txt/pyproject.toml file. " +
        "Returns a report showing what was found, cached, queued, and unresolved.")]
    public static async Task<string> IndexProjectDependencies(
        DependencyIndexer indexer,
        [Description("Project root directory or specific project file path")] string path,
        [Description("Optional database profile name")] string? profile = null,
        CancellationToken ct = default)
    {
        var report = await indexer.IndexProjectAsync(path, profile, ct);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        return json;
    }
}
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build E:/Projects/RAG/SaddleRAG.slnx`
Expected: Fails because `DependencyIndexer` doesn't exist yet. That's fine — it will compile once Task 8 is done. Verify `ScrapeJobFactory` compiles by building Ingestion alone:
Run: `dotnet build E:/Projects/RAG/SaddleRAG.Ingestion/SaddleRAG.Ingestion.csproj`

- [ ] **Step 4: Commit**

Message: `Add ScrapeJobFactory and scrape_docs/index_project_dependencies MCP tools`

---

## Task 4: PackageFilter (Shared Skip Lists)

**Files:**
- Create: `SaddleRAG.Ingestion/Scanning/PackageFilter.cs`
- Delete: `SaddleRAG.Ingestion/Scanning/ProjectScanner.cs`

- [ ] **Step 1: Create PackageFilter**

```csharp
// PackageFilter.cs
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.

using SaddleRAG.Core.Models;

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>
///     Filters out framework, tooling, and test packages that don't need
///     documentation scraping. Per-ecosystem skip lists.
/// </summary>
public class PackageFilter
{
    private static readonly Dictionary<string, IReadOnlyList<string>> smSkipPrefixes = new()
    {
        ["nuget"] =
        [
            "Microsoft.Extensions.", "Microsoft.AspNetCore.",
            "Microsoft.EntityFrameworkCore.", "System.",
            "Microsoft.NET.", "Microsoft.NETCore.", "NETStandard.",
            "xunit", "NUnit", "Moq", "FluentAssertions",
            "coverlet.", "Microsoft.TestPlatform", "MSTest.",
            "CodeStructure.Analyzers"
        ],
        ["npm"] =
        [
            "@types/", "eslint", "prettier", "typescript",
            "webpack", "jest", "mocha", "babel",
            "postcss", "autoprefixer", "vite", "rollup"
        ],
        ["pip"] =
        [
            "setuptools", "pip", "wheel", "pytest",
            "black", "flake8", "mypy", "pylint",
            "isort", "tox", "coverage", "twine"
        ]
    };

    /// <summary>
    ///     Filter a list of dependencies, removing framework and tooling packages.
    /// </summary>
    public IReadOnlyList<PackageDependency> Filter(IReadOnlyList<PackageDependency> dependencies)
    {
        var result = dependencies
            .Where(d => !ShouldSkip(d))
            .ToList();
        return result;
    }

    private static bool ShouldSkip(PackageDependency dependency)
    {
        bool result = false;
        if (smSkipPrefixes.TryGetValue(dependency.EcosystemId, out var prefixes))
        {
            result = prefixes.Any(prefix =>
                dependency.PackageId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
        return result;
    }
}
```

- [ ] **Step 2: Delete ProjectScanner.cs**

Run: `rm E:/Projects/RAG/SaddleRAG.Ingestion/Scanning/ProjectScanner.cs`

- [ ] **Step 3: Delete ProjectTools.cs**

Run: `rm E:/Projects/RAG/SaddleRAG.Mcp/Tools/ProjectTools.cs`

The `scan_project` MCP tool is superseded by `index_project_dependencies` in `ScrapeDocsTools.cs`.

- [ ] **Step 4: Remove ProjectScanner and ProjectTools registrations from Program.cs**

In `SaddleRAG.Mcp/Program.cs`, remove:
```csharp
builder.Services.AddSingleton<ProjectScanner>();
```

- [ ] **Step 5: Build and verify**

Run: `dotnet build E:/Projects/RAG/SaddleRAG.Ingestion/SaddleRAG.Ingestion.csproj`
Expected: Build succeeded (Mcp will still fail until DependencyIndexer exists)

- [ ] **Step 6: Commit**

Message: `Add PackageFilter, remove ProjectScanner and scan_project tool`

---

## Task 5: NuGet Ecosystem Implementation

**Files:**
- Create: `SaddleRAG.Ingestion/Ecosystems/NuGet/NuGetProjectFileParser.cs`
- Create: `SaddleRAG.Ingestion/Ecosystems/NuGet/NuGetRegistryClient.cs`
- Create: `SaddleRAG.Ingestion/Ecosystems/NuGet/NuGetDocUrlResolver.cs`
- Create: `SaddleRAG.Ingestion/Ecosystems/Common/CommonDocUrlPatterns.cs`

- [ ] **Step 1: Create CommonDocUrlPatterns (shared fallback logic)**

```csharp
// CommonDocUrlPatterns.cs
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.

using System.Text.RegularExpressions;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using Microsoft.Extensions.Logging;

namespace SaddleRAG.Ingestion.Ecosystems.Common;

/// <summary>
///     Shared documentation URL resolution fallback patterns.
///     Used by all ecosystem resolvers when registry metadata
///     doesn't provide a direct documentation URL.
/// </summary>
public class CommonDocUrlPatterns
{
    private const int HttpTimeoutMs = 5000;

    private readonly HttpClient mHttpClient;
    private readonly ILogger<CommonDocUrlPatterns> mLogger;

    public CommonDocUrlPatterns(
        IHttpClientFactory httpClientFactory,
        ILogger<CommonDocUrlPatterns> logger)
    {
        mHttpClient = httpClientFactory.CreateClient("DocUrlProbe");
        mHttpClient.Timeout = TimeSpan.FromMilliseconds(HttpTimeoutMs);
        mLogger = logger;
    }

    /// <summary>
    ///     Check if a URL points to a GitHub repository.
    /// </summary>
    public static bool IsGitHubRepo(string url)
    {
        var result = Regex.IsMatch(
            url,
            @"^https?://(?:www\.)?github\.com/[^/]+/[^/]+",
            RegexOptions.IgnoreCase);
        return result;
    }

    /// <summary>
    ///     Try common documentation URL patterns for a package.
    ///     Returns the first URL that responds with HTTP 200.
    /// </summary>
    public async Task<DocUrlResolution> TryCommonPatternsAsync(
        string packageId,
        CancellationToken ct = default)
    {
        var normalizedName = packageId.ToLowerInvariant().Replace(".", "-");
        string[] candidates =
        [
            $"https://{normalizedName}.readthedocs.io",
            $"https://docs.{normalizedName}.com",
            $"https://{normalizedName}.github.io",
            $"https://{normalizedName}.dev"
        ];

        DocUrlResolution result = new()
        {
            DocUrl = null,
            Source = "none",
            Confidence = ScanConfidence.Low
        };

        foreach (var candidate in candidates)
        {
            if (result.DocUrl == null)
            {
                var reachable = await IsReachableAsync(candidate, ct);
                if (reachable)
                {
                    result = new DocUrlResolution
                    {
                        DocUrl = candidate,
                        Source = "pattern",
                        Confidence = ScanConfidence.Medium
                    };
                }
            }
        }

        return result;
    }

    /// <summary>
    ///     Probe a URL to check if it returns HTTP 200.
    /// </summary>
    public async Task<bool> IsReachableAsync(string url, CancellationToken ct = default)
    {
        bool result = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await mHttpClient.SendAsync(request, ct);
            result = response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            mLogger.LogDebug(ex, "URL probe failed for {Url}", url);
        }
        return result;
    }
}
```

- [ ] **Step 2: Create NuGetProjectFileParser**

Refactored from existing `ProjectScanner`. Same `.sln`/`.csproj` parsing logic, now behind `IProjectFileParser`.

```csharp
// NuGetProjectFileParser.cs
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.

using System.Text.RegularExpressions;
using System.Xml.Linq;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using Microsoft.Extensions.Logging;

namespace SaddleRAG.Ingestion.Ecosystems.NuGet;

/// <summary>
///     Parses .NET project files (.sln, .slnx, .csproj) to discover NuGet dependencies.
/// </summary>
public class NuGetProjectFileParser : IProjectFileParser
{
    private const string Ecosystem = "nuget";

    private readonly ILogger<NuGetProjectFileParser> mLogger;

    public NuGetProjectFileParser(ILogger<NuGetProjectFileParser> logger)
    {
        mLogger = logger;
    }

    /// <inheritdoc />
    public string EcosystemId => Ecosystem;

    /// <inheritdoc />
    public IReadOnlyList<string> FilePatterns => ["*.sln", "*.slnx", "*.csproj"];

    /// <inheritdoc />
    public async Task<IReadOnlyList<PackageDependency>> ParseAsync(
        string filePath,
        CancellationToken ct = default)
    {
        var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        switch (extension)
        {
            case ".sln":
            case ".slnx":
            {
                var csprojPaths = ParseSolutionFile(filePath);
                foreach (var csproj in csprojPaths)
                {
                    var packages = await ParseCsprojAsync(csproj, ct);
                    foreach (var (id, version) in packages)
                    {
                        if (!dependencies.ContainsKey(id))
                            dependencies[id] = version;
                    }
                }
                break;
            }
            case ".csproj":
            {
                var packages = await ParseCsprojAsync(filePath, ct);
                foreach (var (id, version) in packages)
                {
                    dependencies[id] = version;
                }
                break;
            }
        }

        mLogger.LogInformation(
            "Parsed {Path}: found {Count} NuGet dependencies", filePath, dependencies.Count);

        var result = dependencies
            .Select(kv => new PackageDependency
            {
                PackageId = kv.Key,
                Version = kv.Value,
                EcosystemId = Ecosystem
            })
            .ToList();
        return result;
    }

    private static IReadOnlyList<string> ParseSolutionFile(string slnPath)
    {
        var slnDir = Path.GetDirectoryName(slnPath) ?? string.Empty;
        var lines = File.ReadAllLines(slnPath);
        var projects = new List<string>();

        foreach (var line in lines)
        {
            var match = Regex.Match(line, @"Project\("".+""\)\s*=\s*"".+"",\s*""(.+\.csproj)""");
            if (match.Success)
            {
                var relativePath = match.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar);
                var fullPath = Path.GetFullPath(Path.Combine(slnDir, relativePath));
                if (File.Exists(fullPath))
                    projects.Add(fullPath);
            }
        }

        return projects;
    }

    private static async Task<IReadOnlyList<(string PackageId, string Version)>> ParseCsprojAsync(
        string csprojPath,
        CancellationToken ct)
    {
        IReadOnlyList<(string PackageId, string Version)> result = [];
        if (File.Exists(csprojPath))
        {
            var xml = await File.ReadAllTextAsync(csprojPath, ct);
            var doc = XDocument.Parse(xml);
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

            result = doc.Descendants(ns + "PackageReference")
                .Select(pr =>
                {
                    var id = pr.Attribute("Include")?.Value ?? string.Empty;
                    var version = pr.Attribute("Version")?.Value
                        ?? pr.Element(ns + "Version")?.Value
                        ?? string.Empty;
                    return (PackageId: id, Version: version);
                })
                .Where(p => !string.IsNullOrEmpty(p.PackageId))
                .ToList();
        }
        return result;
    }
}
```

- [ ] **Step 3: Create NuGetRegistryClient**

```csharp
// NuGetRegistryClient.cs
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.

using System.Text.Json;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using Microsoft.Extensions.Logging;

namespace SaddleRAG.Ingestion.Ecosystems.NuGet;

/// <summary>
///     Fetches package metadata from the NuGet v3 registration API.
/// </summary>
public class NuGetRegistryClient : IPackageRegistryClient
{
    private const string Ecosystem = "nuget";
    private const string RegistrationBaseUrl = "https://api.nuget.org/v3/registration5-gz-semver2";
    private const int TimeoutMs = 5000;

    private readonly HttpClient mHttpClient;
    private readonly ILogger<NuGetRegistryClient> mLogger;

    public NuGetRegistryClient(
        IHttpClientFactory httpClientFactory,
        ILogger<NuGetRegistryClient> logger)
    {
        mHttpClient = httpClientFactory.CreateClient("NuGet");
        mHttpClient.Timeout = TimeSpan.FromMilliseconds(TimeoutMs);
        mLogger = logger;
    }

    /// <inheritdoc />
    public string EcosystemId => Ecosystem;

    /// <inheritdoc />
    public async Task<PackageMetadata?> FetchMetadataAsync(
        string packageId,
        string version,
        CancellationToken ct = default)
    {
        PackageMetadata? result = null;
        try
        {
            var url = $"{RegistrationBaseUrl}/{packageId.ToLowerInvariant()}/{version.ToLowerInvariant()}.json";
            var json = await mHttpClient.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var catalogEntry = root.TryGetProperty("catalogEntry", out var entry) ? entry : root;

            var projectUrl = GetStringProperty(catalogEntry, "projectUrl");
            var description = GetStringProperty(catalogEntry, "description");
            var repository = string.Empty;
            if (catalogEntry.TryGetProperty("repository", out var repoProp))
            {
                repository = GetStringProperty(repoProp, "url");
            }

            result = new PackageMetadata
            {
                PackageId = packageId,
                Version = version,
                EcosystemId = Ecosystem,
                ProjectUrl = projectUrl,
                RepositoryUrl = repository,
                Description = description
            };

            mLogger.LogDebug("Fetched NuGet metadata for {Package} v{Version}", packageId, version);
        }
        catch (Exception ex)
        {
            mLogger.LogWarning(ex, "Failed to fetch NuGet metadata for {Package} v{Version}", packageId, version);
        }
        return result;
    }

    private static string GetStringProperty(JsonElement element, string propertyName)
    {
        string result = string.Empty;
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            result = prop.GetString() ?? string.Empty;
        }
        return result;
    }
}
```

- [ ] **Step 4: Create NuGetDocUrlResolver**

```csharp
// NuGetDocUrlResolver.cs
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Ecosystems.Common;
using Microsoft.Extensions.Logging;

namespace SaddleRAG.Ingestion.Ecosystems.NuGet;

/// <summary>
///     Resolves NuGet package metadata into a documentation URL.
///     Tries registry URLs first, then common patterns.
/// </summary>
public class NuGetDocUrlResolver : IDocUrlResolver
{
    private const string Ecosystem = "nuget";

    private readonly CommonDocUrlPatterns mCommonPatterns;
    private readonly ILogger<NuGetDocUrlResolver> mLogger;

    public NuGetDocUrlResolver(
        CommonDocUrlPatterns commonPatterns,
        ILogger<NuGetDocUrlResolver> logger)
    {
        mCommonPatterns = commonPatterns;
        mLogger = logger;
    }

    /// <inheritdoc />
    public string EcosystemId => Ecosystem;

    /// <inheritdoc />
    public async Task<DocUrlResolution> ResolveAsync(
        PackageMetadata metadata,
        CancellationToken ct = default)
    {
        // Try documentation URL from registry
        DocUrlResolution result = new()
        {
            DocUrl = null,
            Source = "none",
            Confidence = ScanConfidence.Low
        };

        if (!string.IsNullOrEmpty(metadata.DocumentationUrl)
            && !CommonDocUrlPatterns.IsGitHubRepo(metadata.DocumentationUrl))
        {
            result = new DocUrlResolution
            {
                DocUrl = metadata.DocumentationUrl,
                Source = "registry",
                Confidence = ScanConfidence.High
            };
        }

        // Try project URL if not GitHub
        if (result.DocUrl == null
            && !string.IsNullOrEmpty(metadata.ProjectUrl)
            && !CommonDocUrlPatterns.IsGitHubRepo(metadata.ProjectUrl))
        {
            var reachable = await mCommonPatterns.IsReachableAsync(metadata.ProjectUrl, ct);
            if (reachable)
            {
                result = new DocUrlResolution
                {
                    DocUrl = metadata.ProjectUrl,
                    Source = "registry",
                    Confidence = ScanConfidence.Medium
                };
            }
        }

        // If we have a GitHub repo URL, use it as a fallback for repo scraping
        if (result.DocUrl == null)
        {
            var repoUrl = !string.IsNullOrEmpty(metadata.RepositoryUrl)
                ? metadata.RepositoryUrl
                : (CommonDocUrlPatterns.IsGitHubRepo(metadata.ProjectUrl) ? metadata.ProjectUrl : null);

            if (repoUrl != null)
            {
                result = new DocUrlResolution
                {
                    DocUrl = repoUrl,
                    Source = "github-repo",
                    Confidence = ScanConfidence.Medium
                };
            }
        }

        // Try common patterns as last resort
        if (result.DocUrl == null)
        {
            result = await mCommonPatterns.TryCommonPatternsAsync(metadata.PackageId, ct);
        }

        mLogger.LogDebug(
            "Resolved {Package}: {Url} (source={Source}, confidence={Confidence})",
            metadata.PackageId, result.DocUrl ?? "(none)", result.Source, result.Confidence);

        return result;
    }
}
```

- [ ] **Step 5: Build and verify**

Run: `dotnet build E:/Projects/RAG/SaddleRAG.Ingestion/SaddleRAG.Ingestion.csproj`
Expected: Build succeeded

- [ ] **Step 6: Commit**

Message: `Add NuGet ecosystem (parser, registry client, doc resolver) and CommonDocUrlPatterns`

---

## Task 6: npm Ecosystem Implementation

**Files:**
- Create: `SaddleRAG.Ingestion/Ecosystems/Npm/NpmProjectFileParser.cs`
- Create: `SaddleRAG.Ingestion/Ecosystems/Npm/NpmRegistryClient.cs`
- Create: `SaddleRAG.Ingestion/Ecosystems/Npm/NpmDocUrlResolver.cs`

- [ ] **Step 1: Create NpmProjectFileParser**

```csharp
// NpmProjectFileParser.cs
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.

using System.Text.Json;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using Microsoft.Extensions.Logging;

namespace SaddleRAG.Ingestion.Ecosystems.Npm;

/// <summary>
///     Parses package.json files to discover npm dependencies.
/// </summary>
public class NpmProjectFileParser : IProjectFileParser
{
    private const string Ecosystem = "npm";

    private readonly ILogger<NpmProjectFileParser> mLogger;

    public NpmProjectFileParser(ILogger<NpmProjectFileParser> logger)
    {
        mLogger = logger;
    }

    /// <inheritdoc />
    public string EcosystemId => Ecosystem;

    /// <inheritdoc />
    public IReadOnlyList<string> FilePatterns => ["package.json"];

    /// <inheritdoc />
    public async Task<IReadOnlyList<PackageDependency>> ParseAsync(
        string filePath,
        CancellationToken ct = default)
    {
        var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var json = await File.ReadAllTextAsync(filePath, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        ExtractDependencies(root, "dependencies", dependencies);
        ExtractDependencies(root, "devDependencies", dependencies);

        mLogger.LogInformation(
            "Parsed {Path}: found {Count} npm dependencies", filePath, dependencies.Count);

        var result = dependencies
            .Select(kv => new PackageDependency
            {
                PackageId = kv.Key,
                Version = kv.Value,
                EcosystemId = Ecosystem
            })
            .ToList();
        return result;
    }

    private static void ExtractDependencies(
        JsonElement root,
        string sectionName,
        Dictionary<string, string> target)
    {
        if (root.TryGetProperty(sectionName, out var section)
            && section.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in section.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var version = prop.Value.GetString() ?? string.Empty;
                    // Strip version range prefixes for display (^, ~, >=)
                    if (!target.ContainsKey(prop.Name))
                        target[prop.Name] = version;
                }
            }
        }
    }
}
```

- [ ] **Step 2: Create NpmRegistryClient**

```csharp
// NpmRegistryClient.cs
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.

using System.Text.Json;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using Microsoft.Extensions.Logging;

namespace SaddleRAG.Ingestion.Ecosystems.Npm;

/// <summary>
///     Fetches package metadata from the npm registry.
/// </summary>
public class NpmRegistryClient : IPackageRegistryClient
{
    private const string Ecosystem = "npm";
    private const string RegistryBaseUrl = "https://registry.npmjs.org";
    private const int TimeoutMs = 5000;

    private readonly HttpClient mHttpClient;
    private readonly ILogger<NpmRegistryClient> mLogger;

    public NpmRegistryClient(
        IHttpClientFactory httpClientFactory,
        ILogger<NpmRegistryClient> logger)
    {
        mHttpClient = httpClientFactory.CreateClient("npm");
        mHttpClient.Timeout = TimeSpan.FromMilliseconds(TimeoutMs);
        mLogger = logger;
    }

    /// <inheritdoc />
    public string EcosystemId => Ecosystem;

    /// <inheritdoc />
    public async Task<PackageMetadata?> FetchMetadataAsync(
        string packageId,
        string version,
        CancellationToken ct = default)
    {
        PackageMetadata? result = null;
        try
        {
            // Strip version range prefixes
            var cleanVersion = version.TrimStart('^', '~', '>', '=', '<', ' ');
            var url = $"{RegistryBaseUrl}/{packageId}/{cleanVersion}";
            var json = await mHttpClient.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var homepage = GetStringProperty(root, "homepage");
            var description = GetStringProperty(root, "description");
            var repository = string.Empty;
            if (root.TryGetProperty("repository", out var repoProp))
            {
                repository = repoProp.ValueKind switch
                {
                    JsonValueKind.String => repoProp.GetString() ?? string.Empty,
                    JsonValueKind.Object => GetStringProperty(repoProp, "url"),
                    _ => string.Empty
                };
                // Normalize git+https:// and git:// prefixes
                repository = repository
                    .Replace("git+https://", "https://")
                    .Replace("git+ssh://git@", "https://")
                    .Replace("git://", "https://")
                    .TrimEnd(".git");
            }

            result = new PackageMetadata
            {
                PackageId = packageId,
                Version = cleanVersion,
                EcosystemId = Ecosystem,
                ProjectUrl = homepage,
                RepositoryUrl = repository,
                Description = description
            };

            mLogger.LogDebug("Fetched npm metadata for {Package} v{Version}", packageId, cleanVersion);
        }
        catch (Exception ex)
        {
            mLogger.LogWarning(ex, "Failed to fetch npm metadata for {Package} v{Version}", packageId, version);
        }
        return result;
    }

    private static string GetStringProperty(JsonElement element, string propertyName)
    {
        string result = string.Empty;
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            result = prop.GetString() ?? string.Empty;
        }
        return result;
    }
}
```

- [ ] **Step 3: Create NpmDocUrlResolver**

```csharp
// NpmDocUrlResolver.cs
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Ecosystems.Common;
using Microsoft.Extensions.Logging;

namespace SaddleRAG.Ingestion.Ecosystems.Npm;

/// <summary>
///     Resolves npm package metadata into a documentation URL.
/// </summary>
public class NpmDocUrlResolver : IDocUrlResolver
{
    private const string Ecosystem = "npm";

    private readonly CommonDocUrlPatterns mCommonPatterns;
    private readonly ILogger<NpmDocUrlResolver> mLogger;

    public NpmDocUrlResolver(
        CommonDocUrlPatterns commonPatterns,
        ILogger<NpmDocUrlResolver> logger)
    {
        mCommonPatterns = commonPatterns;
        mLogger = logger;
    }

    /// <inheritdoc />
    public string EcosystemId => Ecosystem;

    /// <inheritdoc />
    public async Task<DocUrlResolution> ResolveAsync(
        PackageMetadata metadata,
        CancellationToken ct = default)
    {
        // npm homepage is often the doc site
        DocUrlResolution result = new()
        {
            DocUrl = null,
            Source = "none",
            Confidence = ScanConfidence.Low
        };

        if (!string.IsNullOrEmpty(metadata.ProjectUrl)
            && !CommonDocUrlPatterns.IsGitHubRepo(metadata.ProjectUrl))
        {
            result = new DocUrlResolution
            {
                DocUrl = metadata.ProjectUrl,
                Source = "registry",
                Confidence = ScanConfidence.High
            };
        }

        // GitHub repo fallback
        if (result.DocUrl == null && !string.IsNullOrEmpty(metadata.RepositoryUrl))
        {
            result = new DocUrlResolution
            {
                DocUrl = metadata.RepositoryUrl,
                Source = "github-repo",
                Confidence = ScanConfidence.Medium
            };
        }

        // Common patterns
        if (result.DocUrl == null)
        {
            result = await mCommonPatterns.TryCommonPatternsAsync(metadata.PackageId, ct);
        }

        mLogger.LogDebug(
            "Resolved {Package}: {Url} (source={Source})",
            metadata.PackageId, result.DocUrl ?? "(none)", result.Source);

        return result;
    }
}
```

- [ ] **Step 4: Build and verify**

Run: `dotnet build E:/Projects/RAG/SaddleRAG.Ingestion/SaddleRAG.Ingestion.csproj`

- [ ] **Step 5: Commit**

Message: `Add npm ecosystem (parser, registry client, doc resolver)`

---

## Task 7: pip Ecosystem Implementation

**Files:**
- Create: `SaddleRAG.Ingestion/Ecosystems/Pip/PipProjectFileParser.cs`
- Create: `SaddleRAG.Ingestion/Ecosystems/Pip/PyPiRegistryClient.cs`
- Create: `SaddleRAG.Ingestion/Ecosystems/Pip/PipDocUrlResolver.cs`

- [ ] **Step 1: Create PipProjectFileParser**

```csharp
// PipProjectFileParser.cs
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.

using System.Text.RegularExpressions;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using Microsoft.Extensions.Logging;

namespace SaddleRAG.Ingestion.Ecosystems.Pip;

/// <summary>
///     Parses requirements.txt and pyproject.toml to discover pip dependencies.
/// </summary>
public class PipProjectFileParser : IProjectFileParser
{
    private const string Ecosystem = "pip";

    private readonly ILogger<PipProjectFileParser> mLogger;

    public PipProjectFileParser(ILogger<PipProjectFileParser> logger)
    {
        mLogger = logger;
    }

    /// <inheritdoc />
    public string EcosystemId => Ecosystem;

    /// <inheritdoc />
    public IReadOnlyList<string> FilePatterns => ["requirements.txt", "pyproject.toml"];

    /// <inheritdoc />
    public async Task<IReadOnlyList<PackageDependency>> ParseAsync(
        string filePath,
        CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(filePath).ToLowerInvariant();
        var dependencies = fileName switch
        {
            "requirements.txt" => await ParseRequirementsTxtAsync(filePath, ct),
            "pyproject.toml" => await ParsePyprojectTomlAsync(filePath, ct),
            _ => new List<PackageDependency>()
        };

        mLogger.LogInformation(
            "Parsed {Path}: found {Count} pip dependencies", filePath, dependencies.Count);

        return dependencies;
    }

    private static async Task<IReadOnlyList<PackageDependency>> ParseRequirementsTxtAsync(
        string filePath,
        CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync(filePath, ct);
        var result = new List<PackageDependency>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            // Skip comments and empty lines
            if (trimmed.Length > 0 && !trimmed.StartsWith('#') && !trimmed.StartsWith('-'))
            {
                var match = Regex.Match(trimmed, @"^([a-zA-Z0-9_.-]+)\s*(?:[=!<>~]+\s*(.+))?$");
                if (match.Success)
                {
                    result.Add(new PackageDependency
                    {
                        PackageId = match.Groups[1].Value,
                        Version = match.Groups[2].Success ? match.Groups[2].Value.Trim() : string.Empty,
                        EcosystemId = Ecosystem
                    });
                }
            }
        }

        return result;
    }

    private static async Task<IReadOnlyList<PackageDependency>> ParsePyprojectTomlAsync(
        string filePath,
        CancellationToken ct)
    {
        var content = await File.ReadAllTextAsync(filePath, ct);
        var result = new List<PackageDependency>();

        // Simple TOML parsing for [project.dependencies] section
        // Look for dependencies = [...] under [project]
        var inProjectSection = false;
        var inDependenciesArray = false;

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed == "[project]")
            {
                inProjectSection = true;
            }
            else if (trimmed.StartsWith('[') && trimmed != "[project]")
            {
                inProjectSection = false;
                inDependenciesArray = false;
            }

            if (inProjectSection && trimmed.StartsWith("dependencies"))
            {
                inDependenciesArray = true;
            }

            if (inDependenciesArray)
            {
                // Match quoted dependency strings like "requests>=2.28.0"
                var match = Regex.Match(trimmed, @"""([a-zA-Z0-9_.-]+)\s*(?:[=!<>~]+\s*(.+?))?""");
                if (match.Success)
                {
                    result.Add(new PackageDependency
                    {
                        PackageId = match.Groups[1].Value,
                        Version = match.Groups[2].Success ? match.Groups[2].Value.Trim() : string.Empty,
                        EcosystemId = Ecosystem
                    });
                }

                if (trimmed.Contains(']'))
                {
                    inDependenciesArray = false;
                }
            }
        }

        return result;
    }
}
```

- [ ] **Step 2: Create PyPiRegistryClient**

```csharp
// PyPiRegistryClient.cs
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.

using System.Text.Json;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using Microsoft.Extensions.Logging;

namespace SaddleRAG.Ingestion.Ecosystems.Pip;

/// <summary>
///     Fetches package metadata from the PyPI JSON API.
/// </summary>
public class PyPiRegistryClient : IPackageRegistryClient
{
    private const string Ecosystem = "pip";
    private const string RegistryBaseUrl = "https://pypi.org/pypi";
    private const int TimeoutMs = 5000;

    private readonly HttpClient mHttpClient;
    private readonly ILogger<PyPiRegistryClient> mLogger;

    public PyPiRegistryClient(
        IHttpClientFactory httpClientFactory,
        ILogger<PyPiRegistryClient> logger)
    {
        mHttpClient = httpClientFactory.CreateClient("PyPI");
        mHttpClient.Timeout = TimeSpan.FromMilliseconds(TimeoutMs);
        mLogger = logger;
    }

    /// <inheritdoc />
    public string EcosystemId => Ecosystem;

    /// <inheritdoc />
    public async Task<PackageMetadata?> FetchMetadataAsync(
        string packageId,
        string version,
        CancellationToken ct = default)
    {
        PackageMetadata? result = null;
        try
        {
            var url = string.IsNullOrEmpty(version)
                ? $"{RegistryBaseUrl}/{packageId}/json"
                : $"{RegistryBaseUrl}/{packageId}/{version}/json";

            var json = await mHttpClient.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var info = doc.RootElement.GetProperty("info");

            var projectUrl = GetStringProperty(info, "home_page");
            var description = GetStringProperty(info, "summary");
            var resolvedVersion = GetStringProperty(info, "version");
            var docUrl = string.Empty;
            var repoUrl = string.Empty;

            if (info.TryGetProperty("project_urls", out var urls) && urls.ValueKind == JsonValueKind.Object)
            {
                docUrl = GetStringProperty(urls, "Documentation");
                if (string.IsNullOrEmpty(docUrl))
                    docUrl = GetStringProperty(urls, "Docs");

                repoUrl = GetStringProperty(urls, "Source");
                if (string.IsNullOrEmpty(repoUrl))
                    repoUrl = GetStringProperty(urls, "Repository");
                if (string.IsNullOrEmpty(repoUrl))
                    repoUrl = GetStringProperty(urls, "Source Code");

                if (string.IsNullOrEmpty(projectUrl))
                    projectUrl = GetStringProperty(urls, "Homepage");
            }

            result = new PackageMetadata
            {
                PackageId = packageId,
                Version = string.IsNullOrEmpty(resolvedVersion) ? version : resolvedVersion,
                EcosystemId = Ecosystem,
                ProjectUrl = projectUrl,
                RepositoryUrl = repoUrl,
                DocumentationUrl = docUrl,
                Description = description
            };

            mLogger.LogDebug("Fetched PyPI metadata for {Package} v{Version}", packageId, version);
        }
        catch (Exception ex)
        {
            mLogger.LogWarning(ex, "Failed to fetch PyPI metadata for {Package} v{Version}", packageId, version);
        }
        return result;
    }

    private static string GetStringProperty(JsonElement element, string propertyName)
    {
        string result = string.Empty;
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            result = prop.GetString() ?? string.Empty;
        }
        return result;
    }
}
```

- [ ] **Step 3: Create PipDocUrlResolver**

```csharp
// PipDocUrlResolver.cs
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Ecosystems.Common;
using Microsoft.Extensions.Logging;

namespace SaddleRAG.Ingestion.Ecosystems.Pip;

/// <summary>
///     Resolves PyPI package metadata into a documentation URL.
///     PyPI often has the richest metadata via project_urls.
/// </summary>
public class PipDocUrlResolver : IDocUrlResolver
{
    private const string Ecosystem = "pip";

    private readonly CommonDocUrlPatterns mCommonPatterns;
    private readonly ILogger<PipDocUrlResolver> mLogger;

    public PipDocUrlResolver(
        CommonDocUrlPatterns commonPatterns,
        ILogger<PipDocUrlResolver> logger)
    {
        mCommonPatterns = commonPatterns;
        mLogger = logger;
    }

    /// <inheritdoc />
    public string EcosystemId => Ecosystem;

    /// <inheritdoc />
    public async Task<DocUrlResolution> ResolveAsync(
        PackageMetadata metadata,
        CancellationToken ct = default)
    {
        // PyPI documentation URL is often exact
        DocUrlResolution result = new()
        {
            DocUrl = null,
            Source = "none",
            Confidence = ScanConfidence.Low
        };

        if (!string.IsNullOrEmpty(metadata.DocumentationUrl))
        {
            result = new DocUrlResolution
            {
                DocUrl = metadata.DocumentationUrl,
                Source = "registry",
                Confidence = ScanConfidence.High
            };
        }

        // Homepage if not GitHub
        if (result.DocUrl == null
            && !string.IsNullOrEmpty(metadata.ProjectUrl)
            && !CommonDocUrlPatterns.IsGitHubRepo(metadata.ProjectUrl))
        {
            result = new DocUrlResolution
            {
                DocUrl = metadata.ProjectUrl,
                Source = "registry",
                Confidence = ScanConfidence.Medium
            };
        }

        // GitHub repo fallback
        if (result.DocUrl == null)
        {
            var repoUrl = !string.IsNullOrEmpty(metadata.RepositoryUrl)
                ? metadata.RepositoryUrl
                : (CommonDocUrlPatterns.IsGitHubRepo(metadata.ProjectUrl) ? metadata.ProjectUrl : null);

            if (repoUrl != null)
            {
                result = new DocUrlResolution
                {
                    DocUrl = repoUrl,
                    Source = "github-repo",
                    Confidence = ScanConfidence.Medium
                };
            }
        }

        // Common patterns — readthedocs is very common for Python
        if (result.DocUrl == null)
        {
            result = await mCommonPatterns.TryCommonPatternsAsync(metadata.PackageId, ct);
        }

        mLogger.LogDebug(
            "Resolved {Package}: {Url} (source={Source})",
            metadata.PackageId, result.DocUrl ?? "(none)", result.Source);

        return result;
    }
}
```

- [ ] **Step 4: Build and verify**

Run: `dotnet build E:/Projects/RAG/SaddleRAG.Ingestion/SaddleRAG.Ingestion.csproj`

- [ ] **Step 5: Commit**

Message: `Add pip ecosystem (parser, PyPI registry client, doc resolver)`

---

## Task 8: DependencyIndexer Orchestrator

**Files:**
- Create: `SaddleRAG.Ingestion/Scanning/DependencyIndexer.cs`

- [ ] **Step 1: Create DependencyIndexer**

```csharp
// DependencyIndexer.cs
// Copyright (c) Jackalope Technologies, Inc. All rights reserved.

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using Microsoft.Extensions.Logging;

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>
///     Orchestrates the full dependency indexing pipeline:
///     detect ecosystems -> parse -> filter -> check cache -> resolve URLs -> scrape.
/// </summary>
public class DependencyIndexer
{
    private readonly IEnumerable<IProjectFileParser> mParsers;
    private readonly IEnumerable<IPackageRegistryClient> mRegistryClients;
    private readonly IEnumerable<IDocUrlResolver> mResolvers;
    private readonly PackageFilter mFilter;
    private readonly ScrapeJobRunner mRunner;
    private readonly RepositoryFactory mRepositoryFactory;
    private readonly ILogger<DependencyIndexer> mLogger;

    public DependencyIndexer(
        IEnumerable<IProjectFileParser> parsers,
        IEnumerable<IPackageRegistryClient> registryClients,
        IEnumerable<IDocUrlResolver> resolvers,
        PackageFilter filter,
        ScrapeJobRunner runner,
        RepositoryFactory repositoryFactory,
        ILogger<DependencyIndexer> logger)
    {
        mParsers = parsers;
        mRegistryClients = registryClients;
        mResolvers = resolvers;
        mFilter = filter;
        mRunner = runner;
        mRepositoryFactory = repositoryFactory;
        mLogger = logger;
    }

    /// <summary>
    ///     Scan a project, resolve doc URLs, and scrape un-cached packages.
    /// </summary>
    public async Task<DependencyIndexReport> IndexProjectAsync(
        string projectPath,
        string? profile = null,
        CancellationToken ct = default)
    {
        // Step 1: Detect project files and parse dependencies
        var projectFiles = DetectProjectFiles(projectPath);
        var allDependencies = new List<PackageDependency>();

        foreach (var (filePath, parser) in projectFiles)
        {
            var deps = await parser.ParseAsync(filePath, ct);
            allDependencies.AddRange(deps);
        }

        // Deduplicate by (PackageId, EcosystemId)
        var unique = allDependencies
            .GroupBy(d => (d.PackageId.ToLowerInvariant(), d.EcosystemId))
            .Select(g => g.First())
            .ToList();

        var totalCount = unique.Count;

        // Step 2: Filter
        var filtered = mFilter.Filter(unique);
        var filteredOutCount = totalCount - filtered.Count;

        // Step 3-6: Process each package
        var libraryRepo = mRepositoryFactory.GetLibraryRepository(profile);
        var libraries = await libraryRepo.GetAllLibrariesAsync(ct);
        var libraryLookup = libraries.ToDictionary(
            l => l.Id,
            l => l,
            StringComparer.OrdinalIgnoreCase);

        var statuses = new List<PackageIndexStatus>();
        int cachedCount = 0;
        int cachedDifferentVersionCount = 0;
        int queuedCount = 0;
        int failedCount = 0;

        foreach (var dep in filtered)
        {
            var status = await ProcessPackageAsync(dep, libraryLookup, profile, ct);
            statuses.Add(status);

            switch (status.Status)
            {
                case "Cached":
                    cachedCount++;
                    break;
                case "CachedDifferentVersion":
                    cachedDifferentVersionCount++;
                    break;
                case "Queued":
                    queuedCount++;
                    break;
                default:
                    failedCount++;
                    break;
            }
        }

        var report = new DependencyIndexReport
        {
            ProjectPath = projectPath,
            TotalDependencies = totalCount,
            FilteredOut = filteredOutCount,
            AlreadyCached = cachedCount,
            CachedDifferentVersion = cachedDifferentVersionCount,
            NewlyQueued = queuedCount,
            ResolutionFailed = failedCount,
            Packages = statuses
        };

        mLogger.LogInformation(
            "Dependency indexing complete for {Path}: {Total} deps, {Filtered} filtered, " +
            "{Cached} cached, {Queued} queued, {Failed} unresolved",
            projectPath, totalCount, filteredOutCount, cachedCount, queuedCount, failedCount);

        return report;
    }

    private IReadOnlyList<(string FilePath, IProjectFileParser Parser)> DetectProjectFiles(string path)
    {
        var results = new List<(string, IProjectFileParser)>();

        // If path is a specific file, match it directly
        if (File.Exists(path))
        {
            var fileName = Path.GetFileName(path);
            foreach (var parser in mParsers)
            {
                if (parser.FilePatterns.Any(p => MatchesPattern(fileName, p)))
                {
                    results.Add((path, parser));
                    break;
                }
            }
        }
        else if (Directory.Exists(path))
        {
            // Walk directory looking for recognized project files
            foreach (var parser in mParsers)
            {
                foreach (var pattern in parser.FilePatterns)
                {
                    var files = Directory.GetFiles(path, pattern, SearchOption.AllDirectories);
                    foreach (var file in files)
                    {
                        // Skip node_modules, bin, obj, etc.
                        if (!IsInSkipDirectory(file))
                        {
                            results.Add((file, parser));
                        }
                    }
                }
            }
        }

        return results;
    }

    private async Task<PackageIndexStatus> ProcessPackageAsync(
        PackageDependency dep,
        Dictionary<string, LibraryRecord> libraryLookup,
        string? profile,
        CancellationToken ct)
    {
        PackageIndexStatus result;

        // Check cache
        if (libraryLookup.TryGetValue(dep.PackageId, out var existing))
        {
            if (existing.AllVersions.Contains(dep.Version))
            {
                result = new PackageIndexStatus
                {
                    PackageId = dep.PackageId,
                    Version = dep.Version,
                    EcosystemId = dep.EcosystemId,
                    Status = "Cached",
                    CachedVersion = existing.CurrentVersion
                };
            }
            else
            {
                result = new PackageIndexStatus
                {
                    PackageId = dep.PackageId,
                    Version = dep.Version,
                    EcosystemId = dep.EcosystemId,
                    Status = "CachedDifferentVersion",
                    CachedVersion = existing.CurrentVersion
                };
            }
        }
        else
        {
            // Resolve and queue
            result = await ResolveAndQueueAsync(dep, profile, ct);
        }

        return result;
    }

    private async Task<PackageIndexStatus> ResolveAndQueueAsync(
        PackageDependency dep,
        string? profile,
        CancellationToken ct)
    {
        PackageIndexStatus result;

        // Fetch metadata
        var client = mRegistryClients.FirstOrDefault(c => c.EcosystemId == dep.EcosystemId);
        if (client == null)
        {
            result = new PackageIndexStatus
            {
                PackageId = dep.PackageId,
                Version = dep.Version,
                EcosystemId = dep.EcosystemId,
                Status = "ResolutionFailed",
                ErrorMessage = $"No registry client for ecosystem '{dep.EcosystemId}'"
            };
        }
        else
        {
            var metadata = await client.FetchMetadataAsync(dep.PackageId, dep.Version, ct);
            if (metadata == null)
            {
                result = new PackageIndexStatus
                {
                    PackageId = dep.PackageId,
                    Version = dep.Version,
                    EcosystemId = dep.EcosystemId,
                    Status = "ResolutionFailed",
                    ErrorMessage = "Registry lookup returned null"
                };
            }
            else
            {
                // Resolve doc URL
                var resolver = mResolvers.FirstOrDefault(r => r.EcosystemId == dep.EcosystemId);
                if (resolver == null)
                {
                    result = new PackageIndexStatus
                    {
                        PackageId = dep.PackageId,
                        Version = dep.Version,
                        EcosystemId = dep.EcosystemId,
                        Status = "ResolutionFailed",
                        ErrorMessage = $"No doc URL resolver for ecosystem '{dep.EcosystemId}'"
                    };
                }
                else
                {
                    var resolution = await resolver.ResolveAsync(metadata, ct);
                    result = await QueueOrReportAsync(dep, resolution, metadata, profile, ct);
                }
            }
        }

        return result;
    }

    private async Task<PackageIndexStatus> QueueOrReportAsync(
        PackageDependency dep,
        DocUrlResolution resolution,
        PackageMetadata metadata,
        string? profile,
        CancellationToken ct)
    {
        PackageIndexStatus result;
        if (resolution.DocUrl == null)
        {
            result = new PackageIndexStatus
            {
                PackageId = dep.PackageId,
                Version = dep.Version,
                EcosystemId = dep.EcosystemId,
                Status = "NoDocumentationFound"
            };
        }
        else
        {
            var job = ScrapeJobFactory.CreateFromUrl(
                resolution.DocUrl,
                dep.PackageId,
                dep.Version,
                metadata.Description);

            var jobId = await mRunner.QueueAsync(job, profile, ct);

            result = new PackageIndexStatus
            {
                PackageId = dep.PackageId,
                Version = dep.Version,
                EcosystemId = dep.EcosystemId,
                Status = "Queued",
                DocUrl = resolution.DocUrl,
                JobId = jobId
            };

            mLogger.LogInformation(
                "Queued scrape for {Package} v{Version}: {Url}",
                dep.PackageId, dep.Version, resolution.DocUrl);
        }
        return result;
    }

    private static bool MatchesPattern(string fileName, string pattern)
    {
        // Simple glob matching for *.ext patterns
        var result = pattern.StartsWith('*')
            ? fileName.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
            : fileName.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        return result;
    }

    private static readonly HashSet<string> smSkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", ".git", ".vs", "dist", "build", "out",
        "__pycache__", ".pytest_cache", "packages", ".nuget", "vendor"
    };

    private static bool IsInSkipDirectory(string filePath)
    {
        var result = filePath
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => smSkipDirectories.Contains(segment));
        return result;
    }
}
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build E:/Projects/RAG/SaddleRAG.Ingestion/SaddleRAG.Ingestion.csproj`

- [ ] **Step 3: Commit**

Message: `Add DependencyIndexer orchestrator for scan-resolve-scrape pipeline`

---

## Task 9: Search Tool Consolidation

**Files:**
- Modify: `SaddleRAG.Mcp/Tools/SearchTools.cs`

- [ ] **Step 1: Update search_docs description, remove get_samples and get_howto, add get_library_overview**

In `SearchTools.cs`:
- Update the `[Description]` for `search_docs` to the richer text from the spec that lists all categories.
- Remove the `GetSamples` method entirely.
- Remove the `GetHowTo` method entirely.
- Add `GetLibraryOverview`:

```csharp
[McpServerTool(Name = "get_library_overview")]
[Description(
    "Get an overview of what a library is and how to get started. " +
    "Returns Overview-category documentation chunks. " +
    "If no Overview content exists, returns the most relevant chunks of any category.")]
public static async Task<string> GetLibraryOverview(
    IVectorSearchProvider vectorSearch,
    IEmbeddingProvider embeddingProvider,
    RepositoryFactory repositoryFactory,
    [Description("Library identifier")] string library,
    [Description("Specific version — defaults to current")] string? version = null,
    [Description("Optional database profile name")] string? profile = null,
    CancellationToken ct = default)
{
    var libraryRepository = repositoryFactory.GetLibraryRepository(profile);
    var resolvedVersion = await LibraryTools.ResolveVersionAsync(libraryRepository, library, version, ct);
    var embeddings = await embeddingProvider.EmbedAsync([$"{library} overview getting started introduction"], ct);

    // Try Overview category first
    var filter = new VectorSearchFilter
    {
        Profile = profile,
        LibraryId = library,
        Version = resolvedVersion,
        Category = DocCategory.Overview
    };

    const int MaxOverviewResults = 5;
    var results = await vectorSearch.SearchAsync(embeddings[0], filter, MaxOverviewResults, ct);

    // Fallback to any category if no Overview chunks
    if (results.Count == 0)
    {
        var fallbackFilter = new VectorSearchFilter
        {
            Profile = profile,
            LibraryId = library,
            Version = resolvedVersion
        };
        results = await vectorSearch.SearchAsync(embeddings[0], fallbackFilter, MaxOverviewResults, ct);
    }

    var response = results.Select(r => new
    {
        r.Chunk.LibraryId,
        r.Chunk.Category,
        r.Chunk.PageTitle,
        r.Chunk.SectionPath,
        r.Chunk.PageUrl,
        r.Chunk.Content,
        r.Score
    });

    var json = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
    return json;
}
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build E:/Projects/RAG/SaddleRAG.Mcp/SaddleRAG.Mcp.csproj`

- [ ] **Step 3: Commit**

Message: `Consolidate search tools: remove get_samples/get_howto, add get_library_overview`

---

## Task 10: DI Registration and Final Wiring

**Files:**
- Modify: `SaddleRAG.Mcp/Program.cs`
- Modify: `SaddleRAG.Cli/Program.cs` (if it references removed types)

- [ ] **Step 1: Update Program.cs DI registrations**

In `SaddleRAG.Mcp/Program.cs`, add after the existing service registrations:

```csharp
// HTTP clients for registry APIs
builder.Services.AddHttpClient("NuGet");
builder.Services.AddHttpClient("npm");
builder.Services.AddHttpClient("PyPI");
builder.Services.AddHttpClient("DocUrlProbe");

// Shared utilities
builder.Services.AddSingleton<SaddleRAG.Ingestion.Ecosystems.Common.CommonDocUrlPatterns>();
builder.Services.AddSingleton<SaddleRAG.Ingestion.Scanning.PackageFilter>();

// NuGet ecosystem
builder.Services.AddSingleton<IProjectFileParser, SaddleRAG.Ingestion.Ecosystems.NuGet.NuGetProjectFileParser>();
builder.Services.AddSingleton<IPackageRegistryClient, SaddleRAG.Ingestion.Ecosystems.NuGet.NuGetRegistryClient>();
builder.Services.AddSingleton<IDocUrlResolver, SaddleRAG.Ingestion.Ecosystems.NuGet.NuGetDocUrlResolver>();

// npm ecosystem
builder.Services.AddSingleton<IProjectFileParser, SaddleRAG.Ingestion.Ecosystems.Npm.NpmProjectFileParser>();
builder.Services.AddSingleton<IPackageRegistryClient, SaddleRAG.Ingestion.Ecosystems.Npm.NpmRegistryClient>();
builder.Services.AddSingleton<IDocUrlResolver, SaddleRAG.Ingestion.Ecosystems.Npm.NpmDocUrlResolver>();

// pip ecosystem
builder.Services.AddSingleton<IProjectFileParser, SaddleRAG.Ingestion.Ecosystems.Pip.PipProjectFileParser>();
builder.Services.AddSingleton<IPackageRegistryClient, SaddleRAG.Ingestion.Ecosystems.Pip.PyPiRegistryClient>();
builder.Services.AddSingleton<IDocUrlResolver, SaddleRAG.Ingestion.Ecosystems.Pip.PipDocUrlResolver>();

// Dependency indexing orchestrator
builder.Services.AddSingleton<SaddleRAG.Ingestion.Scanning.DependencyIndexer>();
```

Remove the `ProjectScanner` registration if it still exists.

Add using statements at the top:
```csharp
using SaddleRAG.Core.Interfaces;
```

- [ ] **Step 2: Update SaddleRAG.Cli/Program.cs**

Remove any references to `ProjectScanner`. If the CLI has a `scan` command that uses `ProjectScanner`, either remove it or update it to use `NuGetProjectFileParser`.

- [ ] **Step 3: Add Microsoft.Extensions.Http to Ingestion project**

In `SaddleRAG.Ingestion/SaddleRAG.Ingestion.csproj`, add:
```xml
<PackageReference Include="Microsoft.Extensions.Http" Version="10.0.0" />
```

- [ ] **Step 4: Build the full solution**

Run: `dotnet build E:/Projects/RAG/SaddleRAG.slnx`
Expected: Build succeeded, 0 warnings, 0 errors

- [ ] **Step 5: Commit**

Message: `Wire up DI for all ecosystems, DependencyIndexer, and HttpClientFactory`

- [ ] **Step 6: Final integration commit**

Stage all remaining changes and create a final commit:
Message: `Complete dependency indexing and simplified scraping feature`
