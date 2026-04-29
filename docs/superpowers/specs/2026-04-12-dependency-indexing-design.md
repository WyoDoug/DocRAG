# Dependency Indexing & Simplified Scraping — Design Spec

**Date:** 2026-04-12
**Status:** Approved
**Scope:** Auto-discover project dependencies, resolve documentation URLs, scrape and cache docs locally, consolidate search tools

---

## Problem

Claude Code needs library documentation to generate good code, but currently the user must manually identify documentation URLs and configure scrape jobs with regex patterns, depth limits, and other parameters. This friction means most project dependencies go un-indexed.

## Solution

An automated pipeline that scans a project, discovers all package dependencies across ecosystems (NuGet, npm, pip), resolves each package to its documentation URL via registry metadata and heuristic patterns, and scrapes everything not already cached. A simplified `scrape_docs` MCP tool handles ad-hoc documentation sites (vendor SDKs, standalone tools) that aren't package manager dependencies.

---

## Architecture

### Approach

Pipeline with ecosystem plugins. Three interfaces per concern (`IProjectFileParser`, `IPackageRegistryClient`, `IDocUrlResolver`) with implementations per ecosystem. A `DependencyIndexer` orchestrator ties them together. A `ScrapeJobFactory` auto-derives crawl configuration from a URL for the simplified scrape path.

### Core Interfaces (SaddleRAG.Core)

#### IProjectFileParser

Parses a project file and returns a list of package dependencies.

```
string EcosystemId { get; }
IReadOnlyList<string> FilePatterns { get; }
Task<IReadOnlyList<PackageDependency>> ParseAsync(string filePath, CancellationToken ct);
```

- `EcosystemId`: `"nuget"`, `"npm"`, `"pip"`
- `FilePatterns`: `["*.csproj", "*.sln", "*.slnx"]`, `["package.json"]`, `["requirements.txt", "pyproject.toml"]`

#### IPackageRegistryClient

Fetches package metadata from the ecosystem's registry API.

```
string EcosystemId { get; }
Task<PackageMetadata?> FetchMetadataAsync(string packageId, string version, CancellationToken ct);
```

Registry endpoints:
- NuGet: `https://api.nuget.org/v3/registration5-gz-semver2/{id}/{version}.json`
- npm: `https://registry.npmjs.org/{package}/{version}`
- PyPI: `https://pypi.org/pypi/{package}/{version}/json`

5-second timeout per request. Returns null on failure (logged, does not abort pipeline).

#### IDocUrlResolver

Turns package metadata into a scrapable documentation URL.

```
string EcosystemId { get; }
Task<DocUrlResolution> ResolveAsync(PackageMetadata metadata, CancellationToken ct);
```

Resolution cascade (per ecosystem, then shared fallbacks):
1. Registry-provided documentation URL (if present and not a GitHub repo)
2. Registry-provided project URL (if rendered docs site, not GitHub)
3. GitHub repo detection: check for `/docs` folder or docs site in README
4. Common patterns: `{package}.readthedocs.io`, `docs.{package}.com`, `{package}.github.io`
5. If all fail: return `DocUrlResolution` with null `DocUrl` and `Source = "none"`

### New Models (SaddleRAG.Core)

#### PackageDependency

```csharp
public record PackageDependency
{
    public required string PackageId { get; init; }
    public required string Version { get; init; }
    public required string EcosystemId { get; init; }
}
```

#### PackageMetadata

```csharp
public record PackageMetadata
{
    public required string PackageId { get; init; }
    public required string Version { get; init; }
    public required string EcosystemId { get; init; }
    public string ProjectUrl { get; init; } = string.Empty;
    public string RepositoryUrl { get; init; } = string.Empty;
    public string DocumentationUrl { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
}
```

#### DocUrlResolution

```csharp
public record DocUrlResolution
{
    public string? DocUrl { get; init; }
    public required string Source { get; init; }       // "registry", "pattern", "github-repo", "none"
    public required ScanConfidence Confidence { get; init; }
}
```

Uses existing `ScanConfidence` enum (High, Medium, Low).

#### DependencyIndexReport

```csharp
public record DependencyIndexReport
{
    public required string ProjectPath { get; init; }
    public required int TotalDependencies { get; init; }
    public required int FilteredOut { get; init; }
    public required int AlreadyCached { get; init; }
    public required int CachedDifferentVersion { get; init; }
    public required int NewlyQueued { get; init; }
    public required int ResolutionFailed { get; init; }
    public required IReadOnlyList<PackageIndexStatus> Packages { get; init; }
}

public record PackageIndexStatus
{
    public required string PackageId { get; init; }
    public required string Version { get; init; }
    public required string EcosystemId { get; init; }
    public required string Status { get; init; }        // "Cached", "CachedDifferentVersion", "Queued", "NoDocumentationFound", "ResolutionFailed"
    public string? DocUrl { get; init; }
    public string? CachedVersion { get; init; }
    public string? ErrorMessage { get; init; }
    public string? JobId { get; init; }
}
```

---

## Ecosystem Implementations (SaddleRAG.Ingestion)

### NuGet

- **Parser (`NuGetProjectFileParser`)**: Refactored from existing `ProjectScanner`. Handles `.sln`, `.slnx`, and `.csproj`. The `.sln` to `.csproj` resolution and `XDocument` `PackageReference` parsing logic is preserved.
- **Registry (`NuGetRegistryClient`)**: Calls NuGet v3 registration API. Extracts `projectUrl`, `licenseUrl`, `description`, and `repository` from the catalog entry.
- **Resolver (`NuGetDocUrlResolver`)**: Checks `documentationUrl` and `projectUrl` first. If GitHub repo detected, delegates to `CommonDocUrlPatterns` for docs site discovery.

### npm

- **Parser (`NpmProjectFileParser`)**: Reads `package.json`, extracts `dependencies` and `devDependencies` keys with versions. Resolves version ranges (`^3.2.0`, `~1.0`) to latest matching via registry API.
- **Registry (`NpmRegistryClient`)**: Calls npm registry JSON endpoint. Extracts `homepage`, `repository.url`, `description`.
- **Resolver (`NpmDocUrlResolver`)**: Checks `homepage` first (often the doc site), then `repository.url`, then shared patterns.

### pip

- **Parser (`PipProjectFileParser`)**: Reads `requirements.txt` (`package==version` lines) and `pyproject.toml` (`[project.dependencies]` section). Resolves unpinned/range versions via PyPI API.
- **Registry (`PyPiRegistryClient`)**: Calls PyPI JSON API. Extracts `project_urls` dict — keys like "Documentation", "Homepage", "Source".
- **Resolver (`PipDocUrlResolver`)**: PyPI metadata is richest. `project_urls["Documentation"]` is often exact. Falls back to `{package}.readthedocs.io`.

### Shared Utilities

#### CommonDocUrlPatterns

Shared fallback logic all three resolvers delegate to:
- Is the URL a GitHub repo? Check for `/docs` folder or docs site in README via GitHub API
- Try `{package}.readthedocs.io`, `docs.{package}.com`, `{package}.github.io`
- Normalize GitHub URLs so `GitHubRepoScraper` can handle them

#### PackageFilter

Extracted from existing `ProjectScanner.smSkipPrefixes`. Per-ecosystem skip lists:

- **NuGet**: `Microsoft.Extensions.*`, `Microsoft.AspNetCore.*`, `Microsoft.EntityFrameworkCore.*`, `System.*`, `Microsoft.NET.*`, `Microsoft.NETCore.*`, `NETStandard.*`, `xunit*`, `NUnit*`, `Moq*`, `FluentAssertions*`, `coverlet.*`, `Microsoft.TestPlatform*`, `MSTest.*`
- **npm**: `@types/*`, `eslint*`, `prettier`, `typescript`, `webpack*`, `jest*`, `mocha*`, `babel*`, `postcss*`, `autoprefixer`
- **pip**: `setuptools`, `pip`, `wheel`, `pytest*`, `black`, `flake8`, `mypy`, `pylint`, `isort`, `tox`

---

## Simplified Scraping: `scrape_docs` Tool

### MCP Tool Signature

```
scrape_docs(
    url: string,                // required - the doc site root
    library_id: string,         // required - unique key for cache
    version: string,            // required - version tag for cache
    hint: string? = null,       // optional - helps the classifier
    max_pages: int = 500,       // optional - safety valve
    fetch_delay_ms: int = 500,  // optional - politeness
    force: bool = false,        // optional - re-scrape even if cached
    profile: string? = null     // optional - database profile
)
```

### ScrapeJobFactory

`ScrapeJobFactory.CreateFromUrl(string url)` auto-derives a `ScrapeJob`:

1. Parses URL into host + path prefix (reuses `PageCrawler.ComputeRootScope` logic)
2. Sets `AllowedUrlPatterns` to the host
3. Sets depth limits using the 3-tier model (see below)
4. Auto-populates `ExcludedUrlPatterns`: `/blog/`, `/pricing/`, `/login/`, `/search`, `/account/`, `/cart/`, `#`, `mailto:`
5. If URL is a GitHub repo, sets a flag so the pipeline delegates to `GitHubRepoScraper`

### 3-Tier Crawl Depth Model

New fields on `ScrapeJob`:
- `SameHostDepth: int = 5` (same domain, different path prefix)
- `OffSiteDepth: int = 1` (different domain entirely)

The existing `OutOfScopeMaxDepth` field is replaced by these two. `PageCrawler` depth assignment becomes:

| Link location | Behavior |
|---|---|
| Same host + same path prefix (in root scope) | Unlimited depth (depth counter = 0) |
| Same host + different path prefix | Capped at `SameHostDepth` (default 5) |
| Different host entirely | Capped at `OffSiteDepth` (default 1) |

### Cache-Hit Behavior

Before scraping, check if `LibraryVersionRecord` exists for `(library_id, version)`:
- If cached and `force = false`: return immediately with "already indexed" message
- If cached and `force = true`: delete existing data and re-scrape
- If not cached: proceed with scrape

---

## DependencyIndexer Orchestrator

### Class: `DependencyIndexer` (SaddleRAG.Ingestion)

Constructor dependencies (all via DI):
- `IEnumerable<IProjectFileParser>` — all registered parsers
- `IEnumerable<IPackageRegistryClient>` — all registered registry clients
- `IEnumerable<IDocUrlResolver>` — all registered resolvers
- `PackageFilter`
- `ScrapeJobRunner`
- `RepositoryFactory`
- `ILogger<DependencyIndexer>`

### Method: `IndexProjectAsync(string projectPath, string? profile, CancellationToken ct)`

Pipeline steps:

1. **Detect ecosystems**: Walk directory tree from `projectPath` looking for recognized files. Map each found file to its ecosystem's parser. Multiple ecosystems can coexist (e.g., .NET backend + React frontend).

2. **Parse**: For each detected file, call `IProjectFileParser.ParseAsync`. Collect all `PackageDependency` objects.

3. **Filter**: Run through `PackageFilter` to drop framework/tooling packages.

4. **Check cache**: For each remaining package, check if `LibraryVersionRecord` exists for `(packageId, version)`. Classify as `Cached`, `CachedDifferentVersion`, or needs processing. Skip exact-match cached packages.

5. **Resolve doc URLs**: For un-cached packages, call `IPackageRegistryClient.FetchMetadataAsync` then `IDocUrlResolver.ResolveAsync`. Registry calls are sequential per ecosystem to avoid rate limits. Packages where resolution fails are marked `NoDocumentationFound` or `ResolutionFailed`.

6. **Scrape**: For each resolved URL, call `ScrapeJobRunner.QueueAsync` using `ScrapeJobFactory.CreateFromUrl`. Jobs run concurrently bounded by the per-library semaphore.

7. **Return report**: `DependencyIndexReport` with per-package status.

### MCP Tool: `index_project_dependencies`

```
index_project_dependencies(
    path: string,              // project root, .sln, .csproj, or directory
    profile: string? = null
)
```

If `path` is a directory, walks it looking for project files. If specific file, uses that directly. Returns `DependencyIndexReport` as JSON.

---

## Search Tool Consolidation

### Remove

- `get_samples` — redundant with `search_docs(category: "Sample")`
- `get_howto` — redundant with `search_docs(category: "HowTo")`

### Enhance `search_docs`

Updated description:
```
"Search documentation using natural language. Works across all ingested libraries
or filtered to a specific one. Filter by category to narrow results:
- Overview: concepts, architecture, getting started
- HowTo: tutorials, guides, walkthroughs
- Sample: code examples, demos
- ApiReference: class/method/property docs
- ChangeLog: release notes, migration guides
Omit category to search everything."
```

### New Tool: `get_library_overview`

```
get_library_overview(
    library: string,
    version: string? = null,
    profile: string? = null
)
```

Returns Overview-category chunks for a library. If no Overview chunks exist, falls back to the first few chunks of any category sorted by relevance to the library name.

### Final Search Tool Set

| Tool | Purpose |
|---|---|
| `search_docs` | Natural language search, optional library/category/version filters |
| `get_class_reference` | QualifiedName lookup for API reference (different mechanism) |
| `get_library_overview` | Quick "what is this library" overview |

---

## Error Handling

| Scenario | Behavior |
|---|---|
| Registry API unreachable | Package marked `ResolutionFailed` with error message. Other packages continue. |
| Doc URL resolution fails | Package marked `NoDocumentationFound`. LLM can offer manual `scrape_docs`. |
| Registry returns no useful URLs | Falls through to common pattern heuristics. If all 404, marked unresolved. |
| Version range in package.json/requirements.txt | Resolved to latest matching version via registry API. Cache keyed on resolved version. |
| Same package, different cached version | Report shows `CachedDifferentVersion` with both versions. New version gets scraped. |
| Scrape job fails mid-crawl | Standard `ScrapeJobRunner` error handling — job marked Failed, other jobs unaffected. |
| Rate limiting from registry | Sequential per-ecosystem calls (not parallel). 5-second timeout prevents hanging. |
| Mixed-ecosystem monorepo | All ecosystems detected and processed. Deduplication by `(packageId, ecosystemId)`. |

---

## File Organization

### New Files — SaddleRAG.Core

```
Core/Interfaces/IProjectFileParser.cs
Core/Interfaces/IPackageRegistryClient.cs
Core/Interfaces/IDocUrlResolver.cs
Core/Models/PackageDependency.cs
Core/Models/PackageMetadata.cs
Core/Models/DocUrlResolution.cs
Core/Models/DependencyIndexReport.cs
```

### New Files — SaddleRAG.Ingestion

```
Ingestion/Scanning/PackageFilter.cs
Ingestion/Scanning/ScrapeJobFactory.cs
Ingestion/Scanning/DependencyIndexer.cs

Ingestion/Ecosystems/NuGet/NuGetProjectFileParser.cs
Ingestion/Ecosystems/NuGet/NuGetRegistryClient.cs
Ingestion/Ecosystems/NuGet/NuGetDocUrlResolver.cs

Ingestion/Ecosystems/Npm/NpmProjectFileParser.cs
Ingestion/Ecosystems/Npm/NpmRegistryClient.cs
Ingestion/Ecosystems/Npm/NpmDocUrlResolver.cs

Ingestion/Ecosystems/Pip/PipProjectFileParser.cs
Ingestion/Ecosystems/Pip/PyPiRegistryClient.cs
Ingestion/Ecosystems/Pip/PipDocUrlResolver.cs

Ingestion/Ecosystems/Common/CommonDocUrlPatterns.cs
```

### New Files — SaddleRAG.Mcp

```
Mcp/Tools/ScrapeDocsTools.cs
```

### Modified Files

```
Core/Models/ScrapeJob.cs              — add SameHostDepth, OffSiteDepth fields
Ingestion/Crawling/PageCrawler.cs     — 3-tier depth logic
Mcp/Tools/ProjectTools.cs             — index_project_dependencies tool
Mcp/Tools/SearchTools.cs              — remove get_samples/get_howto, add get_library_overview
Mcp/Program.cs                        — DI registrations for all new services + IHttpClientFactory
```

### Deleted Files

```
Ingestion/Scanning/ProjectScanner.cs  — replaced by NuGetProjectFileParser + PackageFilter
Mcp/Tools/ProjectTools.cs (scan_project tool) — replaced by index_project_dependencies in new ScrapeDocsTools.cs
```

Note: The existing `scan_project` MCP tool depends on `ProjectScanner` and only reports ingestion status without triggering scrapes. It is fully superseded by `index_project_dependencies` which scans, resolves, and scrapes in one call. The `index_project_dependencies` tool is placed in `ScrapeDocsTools.cs` alongside `scrape_docs`.

### HTTP Dependencies

Registry clients use `IHttpClientFactory` (named clients per ecosystem). No new NuGet packages needed — `Microsoft.Extensions.Http` is transitively available via existing ASP.NET Core references.
