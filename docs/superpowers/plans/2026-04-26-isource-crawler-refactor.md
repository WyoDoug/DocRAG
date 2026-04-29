# ISourceCrawler Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Introduce an `ISourceCrawler` abstraction so `IngestionOrchestrator` dispatches the crawl stage through an interface keyed by a new `SourceKind` enum on `ScrapeJob`. The existing coding product must continue working unchanged, riding on the abstraction as its first consumer. This unblocks future products (Medical, Engineering, Race) plugging in their own crawlers without touching the pipeline.

**Architecture:**
- New `SourceKind` enum (`Web` initially, with placeholders for `GitHub`, `FileSystem`, `Video`, `PubMed`, `Arxiv`, `CadCompanion`).
- New `ISourceCrawler` interface with `Kind` property and the existing `PageCrawler.CrawlAsync` signature.
- New `ISourceCrawlerRegistry` that resolves an `ISourceCrawler` by `SourceKind` (constructed from `IEnumerable<ISourceCrawler>` registered in DI).
- `ScrapeJob` gains a `SourceKind` property, defaulting to `Web` for backward compatibility.
- `PageCrawler` implements `ISourceCrawler` (`Kind => SourceKind.Web`). Its existing internal delegation to `GitHubRepoScraper` for discovered GitHub links stays as-is — the abstraction is at the entry point, not internal collaboration.
- `IngestionOrchestrator` constructor changes from concrete `PageCrawler` to `ISourceCrawlerRegistry`, dispatching in `RunCrawlStageAsync` based on `job.SourceKind`.
- DI registration in `DocRAG.Mcp/Program.cs` adds `PageCrawler` as `ISourceCrawler` and registers the registry.
- `PageCrawler.DryRunAsync` stays a concrete method on `PageCrawler` — it's web-specific and called directly from `DocRAG.Mcp/Tools/IngestionTools.cs`. Not on the interface.

**Tech Stack:** C# .NET 10, `System.Threading.Channels`, xUnit v3, NSubstitute, MongoDB, Ollama, Playwright.

**Coding standards (from user's global CLAUDE.md, mandatory):**
- Single return / variable pattern. No early returns.
- No `if`/`else`/`if` chains — use `switch` expressions.
- No `continue` — filter or use `if` block.
- Allman braces; 4-space indent; max 120 chars.
- Field prefixes: `m` (private instance), `ps` (private static), `sm` (private static readonly), `pm` (public instance).
- File header pattern (matches existing repo files exactly):
  ```csharp
  // // FileName.cs
  // // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
  // // Use subject to the MIT License.
  ```
- `string.Empty`, not `""`.
- `var` when type obvious from RHS.
- Argument validation at method entry: `ArgumentNullException.ThrowIfNull(...)`, `ArgumentException.ThrowIfNullOrEmpty(...)`.

---

## File Plan

**Create:**
- `DocRAG.Core/Enums/SourceKind.cs` — enum with all forward-looking source kinds.
- `DocRAG.Core/Interfaces/ISourceCrawler.cs` — entry-point crawler abstraction.
- `DocRAG.Core/Interfaces/ISourceCrawlerRegistry.cs` — lookup interface.
- `DocRAG.Core/Interfaces/IPageDryRunner.cs` — dry-run abstraction extracted from `PageCrawler`.
- `DocRAG.Ingestion/Crawling/SourceCrawlerRegistry.cs` — concrete registry.
- `DocRAG.Tests/Crawling/SourceCrawlerRegistryTests.cs` — unit tests for the registry.
- `DocRAG.Tests/Crawling/PageCrawlerKindTests.cs` — unit test that `PageCrawler.Kind == SourceKind.Web`.
- `DocRAG.Tests/Crawling/PageCrawlerDryRunnerTests.cs` — unit test that `PageCrawler` implements `IPageDryRunner`.
- `DocRAG.Tests/Crawling/GitHubRepoScraperKindTests.cs` — unit tests that `GitHubRepoScraper` implements `ISourceCrawler`, has `Kind == SourceKind.GitHub`, and `CrawlAsync` throws + completes channel on non-GitHub URLs.
- `DocRAG.Tests/Ingestion/IngestionOrchestratorDispatchTests.cs` — unit tests that orchestrator dispatches via registry.

**Modify:**
- `DocRAG.Core/Models/ScrapeJob.cs` — add `SourceKind` property defaulting to `Web`.
- `DocRAG.Ingestion/Crawling/PageCrawler.cs` — implement `ISourceCrawler` and `IPageDryRunner`, add `Kind` property.
- `DocRAG.Ingestion/Crawling/GitHubRepoScraper.cs` — implement `ISourceCrawler`, add `Kind` property and `CrawlAsync` wrapper that parses `job.RootUrl` for owner/repo and completes the channel.
- `DocRAG.Ingestion/IngestionOrchestrator.cs` — replace `PageCrawler crawler` ctor parameter with `ISourceCrawlerRegistry crawlers`; update `RunCrawlStageAsync` to look up crawler by `job.SourceKind`. Drop unused `using DocRAG.Ingestion.Crawling;`.
- `DocRAG.Mcp/Tools/IngestionTools.cs` — change `DryRunScrape` parameter from `PageCrawler crawler` to `IPageDryRunner dryRunner`. Drop unused `using DocRAG.Ingestion.Crawling;`.
- `DocRAG.Mcp/Program.cs` — register `PageCrawler` as both `ISourceCrawler` and `IPageDryRunner`; register `GitHubRepoScraper` as `ISourceCrawler`; register `SourceCrawlerRegistry` as `ISourceCrawlerRegistry`.

**Untouched (intentionally):**
- `ScrapeJobRunner` — already takes `IngestionOrchestrator`; transitive change only.
- The internal collaboration where `PageCrawler` calls `GitHubRepoScraper.ScrapeRepositoryAsync` directly when a GitHub URL is discovered while crawling a docs site. That call path bypasses the registry on purpose — the registry is for entry-point dispatch.

---

### Task 1: Add `SourceKind` enum

**Files:**
- Create: `DocRAG.Core/Enums/SourceKind.cs`

- [ ] **Step 1: Create the enum file**

Write `DocRAG.Core/Enums/SourceKind.cs` with this exact content:

```csharp
// // SourceKind.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

namespace DocRAG.Core.Enums;

/// <summary>
///     Identifies the kind of source a scrape job pulls from.
///     Used by the ingestion pipeline to dispatch to the matching ISourceCrawler.
/// </summary>
public enum SourceKind
{
    /// <summary>
    ///     Default. HTML documentation websites crawled by Playwright.
    /// </summary>
    Web = 0,

    /// <summary>
    ///     A GitHub repository entered as the primary source (cloned, not crawled).
    ///     Reserved for future use; today GitHub URLs are routed through Web crawler internally.
    /// </summary>
    GitHub = 1,

    /// <summary>
    ///     A local or network folder of files (PDF, DOCX, XLSX, MD, TXT, etc.).
    ///     Reserved for the file ingestion product.
    /// </summary>
    FileSystem = 2,

    /// <summary>
    ///     Video files transcribed via Whisper with timestamped segments.
    ///     Reserved for medical/race products.
    /// </summary>
    Video = 3,

    /// <summary>
    ///     PubMed E-utilities API.
    ///     Reserved for medical product.
    /// </summary>
    PubMed = 4,

    /// <summary>
    ///     arXiv API.
    ///     Reserved for medical product.
    /// </summary>
    Arxiv = 5,

    /// <summary>
    ///     CAD vendor companion add-in exporting drawings + parameters + BOM to a watched folder.
    ///     Reserved for engineering/race products.
    /// </summary>
    CadCompanion = 6
}
```

- [ ] **Step 2: Build to verify the enum compiles**

Run: `dotnet build E:/GitHub/DocRAG/DocRAG.Core/DocRAG.Core.csproj`
Expected: Build succeeded, 0 errors, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git -C E:/GitHub/DocRAG add DocRAG.Core/Enums/SourceKind.cs
git -C E:/GitHub/DocRAG commit -F .git-commit-msg.txt
```

Where `.git-commit-msg.txt` contains:

```
Add SourceKind enum for source-type dispatch

Forward-looking enum naming the source types DocRAG products will dispatch
on (Web, GitHub, FileSystem, Video, PubMed, Arxiv, CadCompanion). Web
remains the only kind with a registered crawler in this commit; the rest
are placeholders for upcoming products.
```

---

### Task 2: Add `ISourceCrawler` interface

**Files:**
- Create: `DocRAG.Core/Interfaces/ISourceCrawler.cs`

- [ ] **Step 1: Create the interface file**

Write `DocRAG.Core/Interfaces/ISourceCrawler.cs` with this exact content:

```csharp
// // ISourceCrawler.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using System.Threading.Channels;
using DocRAG.Core.Enums;
using DocRAG.Core.Models;

#endregion

namespace DocRAG.Core.Interfaces;

/// <summary>
///     Entry-point abstraction for the crawl stage of the ingestion pipeline.
///     Each implementation handles one <see cref="SourceKind" /> and emits PageRecords
///     into the supplied channel for downstream classify → chunk → embed → index stages.
/// </summary>
public interface ISourceCrawler
{
    /// <summary>
    ///     Which <see cref="SourceKind" /> this crawler handles. The orchestrator
    ///     dispatches a job to the crawler whose Kind matches <see cref="ScrapeJob.SourceKind" />.
    /// </summary>
    SourceKind Kind { get; }

    /// <summary>
    ///     Crawl the source described by <paramref name="job" /> and write each discovered
    ///     PageRecord to <paramref name="output" />. Implementations must call
    ///     <see cref="ChannelWriter{T}.Complete(Exception?)" /> when crawling finishes
    ///     (success or fail) so downstream stages can drain.
    /// </summary>
    /// <param name="job">The scrape job configuration.</param>
    /// <param name="output">Channel writer that receives discovered pages.</param>
    /// <param name="resumeUrls">URLs already in the DB; used to skip re-fetching during resume.</param>
    /// <param name="onPageFetched">Optional callback invoked with the running fetched page count.</param>
    /// <param name="onQueued">Optional callback invoked with the running queue size.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CrawlAsync(ScrapeJob job,
                    ChannelWriter<PageRecord> output,
                    IReadOnlySet<string>? resumeUrls = null,
                    Action<int>? onPageFetched = null,
                    Action<int>? onQueued = null,
                    CancellationToken ct = default);
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build E:/GitHub/DocRAG/DocRAG.Core/DocRAG.Core.csproj`
Expected: Build succeeded, 0 errors, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git -C E:/GitHub/DocRAG add DocRAG.Core/Interfaces/ISourceCrawler.cs
git -C E:/GitHub/DocRAG commit -F .git-commit-msg.txt
```

Commit message:

```
Add ISourceCrawler interface for crawl-stage dispatch

The crawl stage's entry-point abstraction. Implementations declare which
SourceKind they handle and emit PageRecords into a channel — the existing
PageCrawler.CrawlAsync signature, lifted to an interface so future products
can plug in FileSystem / Video / PubMed / Arxiv crawlers without touching
the orchestrator.
```

---

### Task 3: Add `ISourceCrawlerRegistry` interface

**Files:**
- Create: `DocRAG.Core/Interfaces/ISourceCrawlerRegistry.cs`

- [ ] **Step 1: Create the interface file**

Write `DocRAG.Core/Interfaces/ISourceCrawlerRegistry.cs` with this exact content:

```csharp
// // ISourceCrawlerRegistry.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;

#endregion

namespace DocRAG.Core.Interfaces;

/// <summary>
///     Resolves an <see cref="ISourceCrawler" /> by <see cref="SourceKind" />.
///     Constructed from the set of <see cref="ISourceCrawler" /> implementations
///     registered with DI.
/// </summary>
public interface ISourceCrawlerRegistry
{
    /// <summary>
    ///     Get the crawler registered for the given kind.
    ///     Throws <see cref="InvalidOperationException" /> if no crawler is registered.
    /// </summary>
    ISourceCrawler Get(SourceKind kind);

    /// <summary>
    ///     Try to get the crawler registered for the given kind.
    ///     Returns false (and a null crawler) when none is registered.
    /// </summary>
    bool TryGet(SourceKind kind, out ISourceCrawler? crawler);
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build E:/GitHub/DocRAG/DocRAG.Core/DocRAG.Core.csproj`
Expected: Build succeeded, 0 errors, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git -C E:/GitHub/DocRAG add DocRAG.Core/Interfaces/ISourceCrawlerRegistry.cs
git -C E:/GitHub/DocRAG commit -F .git-commit-msg.txt
```

Commit message:

```
Add ISourceCrawlerRegistry interface

Lookup abstraction over the set of registered ISourceCrawler implementations.
The orchestrator resolves through this rather than holding a concrete
PageCrawler reference, so each new product (Medical, Engineering, Race) just
adds an ISourceCrawler to DI.
```

---

### Task 4: Implement `SourceCrawlerRegistry` with TDD

**Files:**
- Test: `DocRAG.Tests/Crawling/SourceCrawlerRegistryTests.cs`
- Create: `DocRAG.Ingestion/Crawling/SourceCrawlerRegistry.cs`

- [ ] **Step 1: Write the failing tests**

Write `DocRAG.Tests/Crawling/SourceCrawlerRegistryTests.cs` with this exact content:

```csharp
// // SourceCrawlerRegistryTests.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using System.Threading.Channels;
using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using DocRAG.Ingestion.Crawling;

#endregion

namespace DocRAG.Tests.Crawling;

public sealed class SourceCrawlerRegistryTests
{
    [Fact]
    public void GetReturnsRegisteredCrawler()
    {
        var web = new StubCrawler(SourceKind.Web);
        var registry = new SourceCrawlerRegistry([web]);

        var result = registry.Get(SourceKind.Web);

        Assert.Same(web, result);
    }

    [Fact]
    public void GetReturnsCorrectCrawlerWhenMultipleAreRegistered()
    {
        var web = new StubCrawler(SourceKind.Web);
        var files = new StubCrawler(SourceKind.FileSystem);
        var registry = new SourceCrawlerRegistry([web, files]);

        var result = registry.Get(SourceKind.FileSystem);

        Assert.Same(files, result);
    }

    [Fact]
    public void GetThrowsWhenKindNotRegistered()
    {
        var registry = new SourceCrawlerRegistry([new StubCrawler(SourceKind.Web)]);

        Assert.Throws<InvalidOperationException>(() => registry.Get(SourceKind.PubMed));
    }

    [Fact]
    public void TryGetReturnsTrueAndCrawlerWhenRegistered()
    {
        var web = new StubCrawler(SourceKind.Web);
        var registry = new SourceCrawlerRegistry([web]);

        bool found = registry.TryGet(SourceKind.Web, out ISourceCrawler? crawler);

        Assert.True(found);
        Assert.Same(web, crawler);
    }

    [Fact]
    public void TryGetReturnsFalseWhenKindNotRegistered()
    {
        var registry = new SourceCrawlerRegistry([new StubCrawler(SourceKind.Web)]);

        bool found = registry.TryGet(SourceKind.Arxiv, out ISourceCrawler? crawler);

        Assert.False(found);
        Assert.Null(crawler);
    }

    [Fact]
    public void ConstructorThrowsWhenTwoCrawlersShareAKind()
    {
        var a = new StubCrawler(SourceKind.Web);
        var b = new StubCrawler(SourceKind.Web);

        Assert.Throws<ArgumentException>(() => new SourceCrawlerRegistry([a, b]));
    }

    [Fact]
    public void ConstructorThrowsWhenCrawlersIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new SourceCrawlerRegistry(null!));
    }

    private sealed class StubCrawler : ISourceCrawler
    {
        public StubCrawler(SourceKind kind)
        {
            Kind = kind;
        }

        public SourceKind Kind { get; }

        public Task CrawlAsync(ScrapeJob job,
                               ChannelWriter<PageRecord> output,
                               IReadOnlySet<string>? resumeUrls = null,
                               Action<int>? onPageFetched = null,
                               Action<int>? onQueued = null,
                               CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test E:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --filter FullyQualifiedName~SourceCrawlerRegistryTests`
Expected: FAIL — `SourceCrawlerRegistry` type does not exist.

- [ ] **Step 3: Create the implementation**

Write `DocRAG.Ingestion/Crawling/SourceCrawlerRegistry.cs` with this exact content:

```csharp
// // SourceCrawlerRegistry.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;

#endregion

namespace DocRAG.Ingestion.Crawling;

/// <summary>
///     Default <see cref="ISourceCrawlerRegistry" /> built from the set of
///     <see cref="ISourceCrawler" /> implementations registered with DI.
/// </summary>
public sealed class SourceCrawlerRegistry : ISourceCrawlerRegistry
{
    public SourceCrawlerRegistry(IEnumerable<ISourceCrawler> crawlers)
    {
        ArgumentNullException.ThrowIfNull(crawlers);

        var lookup = new Dictionary<SourceKind, ISourceCrawler>();
        foreach(var crawler in crawlers)
        {
            if (lookup.ContainsKey(crawler.Kind))
                throw new ArgumentException($"Multiple ISourceCrawler implementations registered for SourceKind '{crawler.Kind}'.",
                                            nameof(crawlers)
                                           );
            lookup[crawler.Kind] = crawler;
        }

        mCrawlers = lookup;
    }

    private readonly IReadOnlyDictionary<SourceKind, ISourceCrawler> mCrawlers;

    public ISourceCrawler Get(SourceKind kind)
    {
        if (!mCrawlers.TryGetValue(kind, out var crawler))
            throw new InvalidOperationException($"No ISourceCrawler is registered for SourceKind '{kind}'.");

        return crawler;
    }

    public bool TryGet(SourceKind kind, out ISourceCrawler? crawler)
    {
        bool result = mCrawlers.TryGetValue(kind, out var found);
        crawler = found;
        return result;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test E:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --filter FullyQualifiedName~SourceCrawlerRegistryTests`
Expected: PASS — 7/7 tests passing.

- [ ] **Step 5: Commit**

```bash
git -C E:/GitHub/DocRAG add DocRAG.Ingestion/Crawling/SourceCrawlerRegistry.cs DocRAG.Tests/Crawling/SourceCrawlerRegistryTests.cs
git -C E:/GitHub/DocRAG commit -F .git-commit-msg.txt
```

Commit message:

```
Implement SourceCrawlerRegistry with unit tests

Default registry built from the set of ISourceCrawler implementations
registered with DI. Get throws on unknown kind; TryGet returns false.
Constructor rejects duplicate kinds with ArgumentException so DI
mis-registration fails fast.

Tests cover: registered lookup, multi-crawler lookup, unknown kind throws,
TryGet positive/negative, duplicate-kind detection, null-arg validation.
```

---

### Task 5: Add `SourceKind` field to `ScrapeJob`

**Files:**
- Modify: `DocRAG.Core/Models/ScrapeJob.cs`

- [ ] **Step 1: Add `SourceKind` property after `Version` (default `Web`)**

In `DocRAG.Core/Models/ScrapeJob.cs`, after the `Version` property block (currently at lines 35–37) and before the `AllowedUrlPatterns` block, insert:

```csharp
    /// <summary>
    ///     The source type this job pulls from. Determines which ISourceCrawler
    ///     handles ingestion. Defaults to Web for backward compatibility with the
    ///     coding product (HTML documentation crawl).
    /// </summary>
    public SourceKind SourceKind { get; init; } = SourceKind.Web;

```

The full property block, in context (verify by reading the file before/after the edit), should appear between the `Version` block and the `AllowedUrlPatterns` block.

- [ ] **Step 2: Build to verify**

Run: `dotnet build E:/GitHub/DocRAG/DocRAG.Core/DocRAG.Core.csproj`
Expected: Build succeeded, 0 errors, 0 warnings. The existing `using DocRAG.Core.Enums;` import already covers the new property.

- [ ] **Step 3: Build the full solution to confirm no consumer breaks (default makes this backward-compatible)**

Run: `dotnet build E:/GitHub/DocRAG/DocRAG.slnx`
Expected: Build succeeded, 0 errors. All existing `new ScrapeJob { ... }` initializers work without supplying `SourceKind` because it has a default.

- [ ] **Step 4: Commit**

```bash
git -C E:/GitHub/DocRAG add DocRAG.Core/Models/ScrapeJob.cs
git -C E:/GitHub/DocRAG commit -F .git-commit-msg.txt
```

Commit message:

```
Add SourceKind field to ScrapeJob (default Web)

Job-level discriminator that the orchestrator uses to dispatch to the
matching ISourceCrawler. Defaults to Web so existing coding-product scrape
configurations continue to work without change.
```

---

### Task 6: Make `PageCrawler` implement `ISourceCrawler`

**Files:**
- Test: `DocRAG.Tests/Crawling/PageCrawlerKindTests.cs`
- Modify: `DocRAG.Ingestion/Crawling/PageCrawler.cs`

- [ ] **Step 1: Write the failing test**

Write `DocRAG.Tests/Crawling/PageCrawlerKindTests.cs` with this exact content:

```csharp
// // PageCrawlerKindTests.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;
using DocRAG.Ingestion.Crawling;
using Microsoft.Extensions.Logging;

#endregion

namespace DocRAG.Tests.Crawling;

public sealed class PageCrawlerKindTests
{
    [Fact]
    public void PageCrawlerImplementsISourceCrawler()
    {
        Assert.True(typeof(ISourceCrawler).IsAssignableFrom(typeof(PageCrawler)));
    }

    [Fact]
    public void PageCrawlerKindIsWeb()
    {
        var pageRepo = Substitute.For<IPageRepository>();
        var ghLogger = Substitute.For<ILogger<GitHubRepoScraper>>();
        var crawlerLogger = Substitute.For<ILogger<PageCrawler>>();
        var ghScraper = new GitHubRepoScraper(pageRepo, ghLogger);

        var crawler = new PageCrawler(pageRepo, ghScraper, crawlerLogger);

        Assert.Equal(SourceKind.Web, crawler.Kind);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test E:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --filter FullyQualifiedName~PageCrawlerKindTests`
Expected: FAIL — `PageCrawler` does not implement `ISourceCrawler` and has no `Kind` property.

- [ ] **Step 3: Add the interface declaration and `Kind` property to `PageCrawler`**

In `DocRAG.Ingestion/Crawling/PageCrawler.cs`:

a) Update the `using` block (after line 14 `using Microsoft.Extensions.Logging;`) to include the Core enums and interfaces. Replace the existing usings region:

```csharp
#region Usings

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

#endregion
```

(`DocRAG.Core.Enums` is already there; add `DocRAG.Core.Interfaces` if not present.)

b) Change the class declaration (line 28) from:

```csharp
public class PageCrawler
```

to:

```csharp
public class PageCrawler : ISourceCrawler
```

c) Add the `Kind` property. Insert it immediately after the constructor (after the closing `}` at line 58, before the `private readonly GitHubRepoScraper mGitHubScraper;` field):

```csharp
    /// <summary>
    ///     <see cref="ISourceCrawler.Kind" />. Always <see cref="SourceKind.Web" /> for this crawler.
    /// </summary>
    public SourceKind Kind => SourceKind.Web;

```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test E:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --filter FullyQualifiedName~PageCrawlerKindTests`
Expected: PASS — 2/2 tests passing.

- [ ] **Step 5: Build the full solution to confirm `PageCrawler.CrawlAsync` already satisfies the interface signature**

Run: `dotnet build E:/GitHub/DocRAG/DocRAG.slnx`
Expected: Build succeeded, 0 errors. The existing `CrawlAsync(ScrapeJob, ChannelWriter<PageRecord>, IReadOnlySet<string>?, Action<int>?, Action<int>?, CancellationToken)` matches `ISourceCrawler.CrawlAsync` exactly.

- [ ] **Step 6: Commit**

```bash
git -C E:/GitHub/DocRAG add DocRAG.Ingestion/Crawling/PageCrawler.cs DocRAG.Tests/Crawling/PageCrawlerKindTests.cs
git -C E:/GitHub/DocRAG commit -F .git-commit-msg.txt
```

Commit message:

```
PageCrawler implements ISourceCrawler (Kind = Web)

The existing CrawlAsync signature already matches ISourceCrawler exactly,
so this is a one-property addition plus the interface declaration. Internal
delegation to GitHubRepoScraper for discovered GitHub links is unchanged.
DryRunAsync stays a concrete method on PageCrawler — it is web-specific
and called directly from MCP tooling, not part of the abstraction.
```

---

### Task 7: Refactor `IngestionOrchestrator` to dispatch through the registry

**Files:**
- Test: `DocRAG.Tests/Ingestion/IngestionOrchestratorDispatchTests.cs`
- Modify: `DocRAG.Ingestion/IngestionOrchestrator.cs`

- [ ] **Step 1: Write the failing dispatch test**

Write `DocRAG.Tests/Ingestion/IngestionOrchestratorDispatchTests.cs` with this exact content:

```csharp
// // IngestionOrchestratorDispatchTests.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using System.Threading.Channels;
using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using DocRAG.Ingestion;
using DocRAG.Ingestion.Chunking;
using DocRAG.Ingestion.Classification;
using DocRAG.Ingestion.Crawling;
using DocRAG.Ingestion.Embedding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#endregion

namespace DocRAG.Tests.Ingestion;

public sealed class IngestionOrchestratorDispatchTests
{
    [Fact]
    public async Task IngestAsyncResolvesCrawlerByJobSourceKind()
    {
        var capturingCrawler = new CapturingCrawler(SourceKind.Web);
        var registry = new SourceCrawlerRegistry([capturingCrawler]);

        var orchestrator = BuildOrchestrator(registry);

        var job = BuildJob(SourceKind.Web);

        var ct = TestContext.Current.CancellationToken;
        await orchestrator.IngestAsync(job, profile: null, forceClean: true, ct: ct);

        Assert.Equal(1, capturingCrawler.CallCount);
        Assert.Same(job, capturingCrawler.LastJob);
    }

    [Fact]
    public async Task IngestAsyncThrowsWhenNoCrawlerRegisteredForJobKind()
    {
        var registry = new SourceCrawlerRegistry([new CapturingCrawler(SourceKind.Web)]);
        var orchestrator = BuildOrchestrator(registry);
        var job = BuildJob(SourceKind.PubMed);

        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await orchestrator.IngestAsync(job, profile: null, forceClean: true, ct: ct)
        );
    }

    private static IngestionOrchestrator BuildOrchestrator(ISourceCrawlerRegistry registry)
    {
        var llmClassifier = new LlmClassifier(
            Options.Create(new OllamaSettings { Endpoint = "http://localhost:11434" }),
            Substitute.For<ILogger<LlmClassifier>>()
        );

        // CategoryAwareChunker has only a default constructor — it's stateless
        // and its Chunk method is never invoked here because no pages flow.
        var chunker = new CategoryAwareChunker();

        var embeddingProvider = Substitute.For<IEmbeddingProvider>();
        embeddingProvider.ProviderId.Returns("stub");
        embeddingProvider.ModelName.Returns("stub-model");
        embeddingProvider.Dimensions.Returns(8);

        var vectorSearch = Substitute.For<IVectorSearchProvider>();
        var libraryRepo = Substitute.For<ILibraryRepository>();
        libraryRepo.GetLibraryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns((LibraryRecord?) null);

        var pageRepo = Substitute.For<IPageRepository>();
        pageRepo.GetPagesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<PageRecord>());

        var chunkRepo = Substitute.For<IChunkRepository>();

        return new IngestionOrchestrator(registry,
                                         llmClassifier,
                                         chunker,
                                         embeddingProvider,
                                         vectorSearch,
                                         libraryRepo,
                                         pageRepo,
                                         chunkRepo,
                                         Substitute.For<ILogger<IngestionOrchestrator>>()
                                        );
    }

    private static ScrapeJob BuildJob(SourceKind kind) => new()
        {
            RootUrl = "https://docs.example.com/",
            LibraryHint = "test library",
            LibraryId = "test-lib",
            Version = "1.0",
            SourceKind = kind,
            AllowedUrlPatterns = ["docs\\.example\\.com"]
        };

    private sealed class CapturingCrawler : ISourceCrawler
    {
        public CapturingCrawler(SourceKind kind)
        {
            Kind = kind;
        }

        public SourceKind Kind { get; }
        public int CallCount { get; private set; }
        public ScrapeJob? LastJob { get; private set; }

        public Task CrawlAsync(ScrapeJob job,
                               ChannelWriter<PageRecord> output,
                               IReadOnlySet<string>? resumeUrls = null,
                               Action<int>? onPageFetched = null,
                               Action<int>? onQueued = null,
                               CancellationToken ct = default)
        {
            CallCount++;
            LastJob = job;
            output.Complete();
            return Task.CompletedTask;
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test E:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --filter FullyQualifiedName~IngestionOrchestratorDispatchTests`
Expected: FAIL — `IngestionOrchestrator` constructor still takes `PageCrawler` not `ISourceCrawlerRegistry`.

- [ ] **Step 3: Refactor `IngestionOrchestrator` constructor and field**

In `DocRAG.Ingestion/IngestionOrchestrator.cs`:

a) Replace the constructor (lines 27–46) with this exact block:

```csharp
    public IngestionOrchestrator(ISourceCrawlerRegistry crawlers,
                                 LlmClassifier llmClassifier,
                                 CategoryAwareChunker chunker,
                                 IEmbeddingProvider embeddingProvider,
                                 IVectorSearchProvider vectorSearch,
                                 ILibraryRepository libraryRepository,
                                 IPageRepository pageRepository,
                                 IChunkRepository chunkRepository,
                                 ILogger<IngestionOrchestrator> logger)
    {
        ArgumentNullException.ThrowIfNull(crawlers);
        mCrawlers = crawlers;
        mLlmClassifier = llmClassifier;
        mChunker = chunker;
        mEmbeddingProvider = embeddingProvider;
        mVectorSearch = vectorSearch;
        mLibraryRepository = libraryRepository;
        mPageRepository = pageRepository;
        mChunkRepository = chunkRepository;
        mLogger = logger;
    }
```

b) Replace the `private readonly PageCrawler mCrawler;` field (line 51) with:

```csharp
    private readonly ISourceCrawlerRegistry mCrawlers;
```

(Field naming follows the same `m` prefix as the rest. The plural reflects that the registry holds multiple crawlers.)

- [ ] **Step 4: Update `RunCrawlStageAsync` to dispatch through the registry**

In `DocRAG.Ingestion/IngestionOrchestrator.cs`, replace the entire `RunCrawlStageAsync` method (lines 173–206) with:

```csharp
    private async Task RunCrawlStageAsync(ScrapeJob job,
                                          ChannelWriter<PageRecord> output,
                                          IReadOnlySet<string>? resumeUrls,
                                          ScrapeJobRecord progress,
                                          Action<ScrapeJobRecord>? onProgress,
                                          CancellationTokenSource cts)
    {
        try
        {
            var crawler = mCrawlers.Get(job.SourceKind);
            mLogger.LogInformation("Dispatching crawl for {LibraryId} v{Version} to {Crawler} (SourceKind={Kind})",
                                   job.LibraryId,
                                   job.Version,
                                   crawler.GetType().Name,
                                   job.SourceKind
                                  );

            await crawler.CrawlAsync(job,
                                     output,
                                     resumeUrls,
                                     pageCount =>
                                     {
                                         progress.PagesFetched = pageCount;
                                         onProgress?.Invoke(progress);
                                     },
                                     queueCount => { progress.PagesQueued = queueCount; },
                                     cts.Token
                                    );
        }
        catch(OperationCanceledException)
        {
            output.TryComplete();
            throw;
        }
        catch(Exception ex)
        {
            mLogger.LogError(ex, "Crawl stage fatal error");
            output.TryComplete(ex);
            await cts.CancelAsync();
            throw;
        }
    }
```

The only behavioral change vs. the original: the crawler is resolved per-job from the registry, and an info log records which crawler ran. All progress callbacks and exception flow are identical.

- [ ] **Step 5: Update the `using` directives at the top of `IngestionOrchestrator.cs`**

`DocRAG.Core.Interfaces` is already imported (line 9). No change needed; verify by re-reading lines 5–17 of the file. The `DocRAG.Ingestion.Crawling` import (line 13) can be removed if nothing else in the file references that namespace — but leave it, it costs nothing and other crawler-related symbols may resurface.

- [ ] **Step 6: Run the orchestrator dispatch tests to verify they pass**

Run: `dotnet test E:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --filter FullyQualifiedName~IngestionOrchestratorDispatchTests`
Expected: PASS — 2/2 tests passing.

- [ ] **Step 7: Build the full solution**

Run: `dotnet build E:/GitHub/DocRAG/DocRAG.slnx`
Expected: Build fails in `DocRAG.Mcp/Program.cs` because the DI container still wires `IngestionOrchestrator` against the old `PageCrawler` ctor parameter — that's expected and is fixed in Task 8. **Do not commit yet** if `Program.cs` is the only failing file. Otherwise, fix any other consumer the build flagged (none expected — nothing else constructs `IngestionOrchestrator` directly).

If the only failure is in `Program.cs`, proceed to Task 8 before committing this task.

- [ ] **Step 8: Commit (after Task 8 makes the solution build)**

This task's commit happens at the end of Task 8 to keep the tree green. Skip the standalone commit here.

---

### Task 8: Wire DI in `Program.cs`

**Files:**
- Modify: `DocRAG.Mcp/Program.cs`

- [ ] **Step 1: Register `PageCrawler` as `ISourceCrawler` and add the registry**

In `DocRAG.Mcp/Program.cs`, find the existing crawler registration block (lines 187–195):

```csharp
builder.Services.AddSingleton<GitHubRepoScraper>();

builder.Services.AddSingleton<PageCrawler>();

builder.Services.AddSingleton<CategoryAwareChunker>();

builder.Services.AddSingleton<IngestionOrchestrator>();

builder.Services.AddSingleton<ScrapeJobRunner>();
```

Replace it with:

```csharp
builder.Services.AddSingleton<GitHubRepoScraper>();

builder.Services.AddSingleton<PageCrawler>();

// Register PageCrawler as the ISourceCrawler for SourceKind.Web. Future products
// add their own ISourceCrawler implementations here (FileSystem, Video, PubMed, ...).
builder.Services.AddSingleton<ISourceCrawler>(sp => sp.GetRequiredService<PageCrawler>());

builder.Services.AddSingleton<ISourceCrawlerRegistry, SourceCrawlerRegistry>();

builder.Services.AddSingleton<CategoryAwareChunker>();

builder.Services.AddSingleton<IngestionOrchestrator>();

builder.Services.AddSingleton<ScrapeJobRunner>();
```

The forwarding registration (`sp => sp.GetRequiredService<PageCrawler>()`) ensures the **same instance** answers both `PageCrawler` (still injected by `IngestionTools.DryRunScrape` and the GitHub-link delegation inside `PageCrawler` itself) and `ISourceCrawler` (consumed by `SourceCrawlerRegistry`).

- [ ] **Step 2: Verify the using directives at the top of `Program.cs` cover the new types**

`DocRAG.Core.Interfaces` is already imported (line 18 area). `DocRAG.Ingestion.Crawling` is imported (line 28 area). No changes needed.

- [ ] **Step 3: Build the full solution**

Run: `dotnet build E:/GitHub/DocRAG/DocRAG.slnx`
Expected: Build succeeded, 0 errors, 0 warnings.

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test E:/GitHub/DocRAG/DocRAG.slnx --filter "Category!=Integration"`
Expected: PASS — all unit tests green (existing + new `SourceCrawlerRegistryTests`, `PageCrawlerKindTests`, `IngestionOrchestratorDispatchTests`).

(Skip integration tests — they hit live NuGet/npm/PyPI APIs and are not relevant to this refactor.)

- [ ] **Step 5: Commit Tasks 7 + 8 together**

```bash
git -C E:/GitHub/DocRAG add DocRAG.Ingestion/IngestionOrchestrator.cs DocRAG.Tests/Ingestion/IngestionOrchestratorDispatchTests.cs DocRAG.Mcp/Program.cs
git -C E:/GitHub/DocRAG commit -F .git-commit-msg.txt
```

Commit message:

```
Dispatch crawl stage through ISourceCrawlerRegistry

IngestionOrchestrator no longer takes a concrete PageCrawler; it now
resolves the crawler per-job from the registry by ScrapeJob.SourceKind.
PageCrawler is registered as the ISourceCrawler for SourceKind.Web; the
default ScrapeJob.SourceKind = Web means existing coding-product scrapes
work unchanged.

Future products (Medical/Engineering/Race) add their own ISourceCrawler
implementations in DI without touching the orchestrator.

Tests cover registry dispatch and missing-kind error path with stub
crawlers and substituted pipeline dependencies.
```

---

### Task 9: Promote `GitHubRepoScraper` to top-level `ISourceCrawler`

Direct-repo entry — when a user submits a `https://github.com/owner/repo` URL with `SourceKind=GitHub`, the registry routes to `GitHubRepoScraper.CrawlAsync`. The internal collaboration where `PageCrawler` calls `GitHubRepoScraper.ScrapeRepositoryAsync` for *discovered* GitHub links during a Web crawl is unchanged — that call path stays direct and does not flow through the registry.

**Files:**
- Test: `DocRAG.Tests/Crawling/GitHubRepoScraperKindTests.cs`
- Modify: `DocRAG.Ingestion/Crawling/GitHubRepoScraper.cs`
- Modify: `DocRAG.Mcp/Program.cs`

- [ ] **Step 1: Write the failing tests**

Write `DocRAG.Tests/Crawling/GitHubRepoScraperKindTests.cs` with this exact content:

```csharp
// // GitHubRepoScraperKindTests.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using System.Threading.Channels;
using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using DocRAG.Ingestion.Crawling;
using Microsoft.Extensions.Logging;

#endregion

namespace DocRAG.Tests.Crawling;

public sealed class GitHubRepoScraperKindTests
{
    [Fact]
    public void GitHubRepoScraperImplementsISourceCrawler()
    {
        Assert.True(typeof(ISourceCrawler).IsAssignableFrom(typeof(GitHubRepoScraper)));
    }

    [Fact]
    public void GitHubRepoScraperKindIsGitHub()
    {
        var scraper = BuildScraper();

        Assert.Equal(SourceKind.GitHub, scraper.Kind);
    }

    [Fact]
    public async Task CrawlAsyncThrowsWhenRootUrlIsNotGitHub()
    {
        var scraper = BuildScraper();
        var job = BuildJob("https://docs.example.com/intro");
        var channel = Channel.CreateUnbounded<PageRecord>();

        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await scraper.CrawlAsync(job, channel.Writer, ct: ct)
        );
    }

    [Fact]
    public async Task CrawlAsyncCompletesChannelEvenWhenRootUrlIsInvalid()
    {
        var scraper = BuildScraper();
        var job = BuildJob("https://docs.example.com/intro");
        var channel = Channel.CreateUnbounded<PageRecord>();

        var ct = TestContext.Current.CancellationToken;
        try
        {
            await scraper.CrawlAsync(job, channel.Writer, ct: ct);
        }
        catch(InvalidOperationException)
        {
            // Expected.
        }

        Assert.True(channel.Reader.Completion.IsCompleted);
    }

    private static GitHubRepoScraper BuildScraper()
    {
        var pageRepo = Substitute.For<IPageRepository>();
        var logger = Substitute.For<ILogger<GitHubRepoScraper>>();
        return new GitHubRepoScraper(pageRepo, logger);
    }

    private static ScrapeJob BuildJob(string rootUrl) => new()
        {
            RootUrl = rootUrl,
            LibraryHint = "test library",
            LibraryId = "test-lib",
            Version = "1.0",
            SourceKind = SourceKind.GitHub,
            AllowedUrlPatterns = []
        };
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test E:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --filter FullyQualifiedName~GitHubRepoScraperKindTests`
Expected: FAIL — `GitHubRepoScraper` does not implement `ISourceCrawler` and has no `Kind` property or `CrawlAsync` method.

- [ ] **Step 3: Modify `GitHubRepoScraper` class declaration and add the abstraction members**

In `DocRAG.Ingestion/Crawling/GitHubRepoScraper.cs`:

a) Update the `using` block (top of file) to include `System.Threading.Channels` and `DocRAG.Core.Enums` (if not already). The full usings region should read:

```csharp
#region Usings

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using Microsoft.Extensions.Logging;

#endregion
```

(`System.Threading.Channels` is already present; `DocRAG.Core.Enums` may or may not be — add if missing.)

b) Change the class declaration (line 28) from:

```csharp
public class GitHubRepoScraper
```

to:

```csharp
public class GitHubRepoScraper : ISourceCrawler
```

c) Insert the `Kind` property and the `CrawlAsync` method immediately after the constructor (after the closing `}` of the constructor at line 34, before the `private readonly ILogger<GitHubRepoScraper> mLogger;` field):

```csharp
    /// <summary>
    ///     <see cref="ISourceCrawler.Kind" />. Always <see cref="SourceKind.GitHub" /> for this crawler.
    /// </summary>
    public SourceKind Kind => SourceKind.GitHub;

    /// <summary>
    ///     <see cref="ISourceCrawler.CrawlAsync" /> entry point for direct-GitHub-URL scrapes.
    ///     Parses owner/repo from <see cref="ScrapeJob.RootUrl" />, delegates to
    ///     <see cref="ScrapeRepositoryAsync" />, and completes the output channel
    ///     when finished (success or failure).
    /// </summary>
    /// <remarks>
    ///     Per-page progress callbacks (<paramref name="onPageFetched" />, <paramref name="onQueued" />)
    ///     are intentionally not wired through to <see cref="ScrapeRepositoryAsync" /> in this revision —
    ///     keeping the existing public method signature untouched. The orchestrator's downstream
    ///     stages still report `ChunksGenerated`, `ChunksEmbedded`, and `PagesCompleted` accurately;
    ///     only `PagesFetched` will read 0 during a direct-GitHub crawl. Threading the callback
    ///     through is a follow-up improvement, not a correctness gap.
    /// </remarks>
    public async Task CrawlAsync(ScrapeJob job,
                                 ChannelWriter<PageRecord> output,
                                 IReadOnlySet<string>? resumeUrls = null,
                                 Action<int>? onPageFetched = null,
                                 Action<int>? onQueued = null,
                                 CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(output);

        try
        {
            if (!TryParseGitHubUrl(job.RootUrl, out string owner, out string repo))
                throw new InvalidOperationException($"GitHubRepoScraper requires a github.com URL; got '{job.RootUrl}'.");

            await ScrapeRepositoryAsync(owner, repo, job, output, ct);
        }
        finally
        {
            output.TryComplete();
        }
    }

```

The internal `ScrapeRepositoryAsync` method is unchanged — `PageCrawler` continues to call it directly for GitHub URLs discovered during a Web crawl, and `PageCrawler.CrawlAsync` continues to own channel completion in that path.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test E:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --filter FullyQualifiedName~GitHubRepoScraperKindTests`
Expected: PASS — 4/4 tests passing.

- [ ] **Step 5: Register `GitHubRepoScraper` as `ISourceCrawler` in DI**

In `DocRAG.Mcp/Program.cs`, find the `ISourceCrawler` registration block added in Task 8:

```csharp
// Register PageCrawler as the ISourceCrawler for SourceKind.Web. Future products
// add their own ISourceCrawler implementations here (FileSystem, Video, PubMed, ...).
builder.Services.AddSingleton<ISourceCrawler>(sp => sp.GetRequiredService<PageCrawler>());

builder.Services.AddSingleton<ISourceCrawlerRegistry, SourceCrawlerRegistry>();
```

Insert a second `ISourceCrawler` registration for `GitHubRepoScraper` between the PageCrawler line and the registry line, so the block reads:

```csharp
// Register PageCrawler as the ISourceCrawler for SourceKind.Web, and GitHubRepoScraper
// as the ISourceCrawler for SourceKind.GitHub. Future products add their own
// ISourceCrawler implementations here (FileSystem, Video, PubMed, ...).
builder.Services.AddSingleton<ISourceCrawler>(sp => sp.GetRequiredService<PageCrawler>());

builder.Services.AddSingleton<ISourceCrawler>(sp => sp.GetRequiredService<GitHubRepoScraper>());

builder.Services.AddSingleton<ISourceCrawlerRegistry, SourceCrawlerRegistry>();
```

The forwarding registration ensures the same `GitHubRepoScraper` instance answers both `GitHubRepoScraper` (still injected by `PageCrawler` for internal delegation) and `ISourceCrawler` (consumed by the registry).

- [ ] **Step 6: Build the full solution**

Run: `dotnet build E:/GitHub/DocRAG/DocRAG.slnx`
Expected: Build succeeded, 0 errors, 0 warnings.

- [ ] **Step 7: Run the full unit test suite**

Run: `dotnet test E:/GitHub/DocRAG/DocRAG.slnx --filter "Category!=Integration"`
Expected: All unit tests pass (existing + 4 new tests in `GitHubRepoScraperKindTests`).

- [ ] **Step 8: Commit**

```bash
git -C E:/GitHub/DocRAG add DocRAG.Ingestion/Crawling/GitHubRepoScraper.cs DocRAG.Tests/Crawling/GitHubRepoScraperKindTests.cs DocRAG.Mcp/Program.cs
git -C E:/GitHub/DocRAG commit -F .git-commit-msg.txt
```

Commit message:

```
GitHubRepoScraper implements ISourceCrawler (Kind = GitHub)

Direct-GitHub-URL entry: a ScrapeJob with SourceKind=GitHub now routes
through the registry to GitHubRepoScraper.CrawlAsync, which parses
owner/repo from RootUrl, delegates to the existing ScrapeRepositoryAsync,
and completes the channel.

The internal collaboration where PageCrawler calls ScrapeRepositoryAsync
for GitHub links discovered while crawling a docs site is unchanged —
that path stays direct and does not flow through the registry.

Per-page progress (PagesFetched) is not threaded through ScrapeRepositoryAsync
in this revision; ChunksGenerated/Embedded/Completed report normally.

Tests: ISourceCrawler implementation, Kind == GitHub, CrawlAsync throws
on non-GitHub RootUrl, channel completes even on parse failure.
```

---

### Task 10: Extract `IPageDryRunner` from `PageCrawler`

The MCP `dryrun_scrape` tool today depends on the concrete `PageCrawler`. Extracting an `IPageDryRunner` interface lets the tool depend on an abstraction and lets future crawlers (FileSystem, Video, ...) opt into dry-run support without touching the MCP tool.

**Files:**
- Create: `DocRAG.Core/Interfaces/IPageDryRunner.cs`
- Test: `DocRAG.Tests/Crawling/PageCrawlerDryRunnerTests.cs`
- Modify: `DocRAG.Ingestion/Crawling/PageCrawler.cs`
- Modify: `DocRAG.Mcp/Tools/IngestionTools.cs`
- Modify: `DocRAG.Mcp/Program.cs`

- [ ] **Step 1: Create the interface**

Write `DocRAG.Core/Interfaces/IPageDryRunner.cs` with this exact content:

```csharp
// // IPageDryRunner.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using DocRAG.Core.Models;

#endregion

namespace DocRAG.Core.Interfaces;

/// <summary>
///     Dry-runs a scrape configuration without writing to MongoDB or cloning
///     external repos. Reports page counts, depth distribution, sample pages,
///     fetch errors, and out-of-scope GitHub repos that would be crawled.
///     Implemented by crawlers that can preview their work; today only the
///     web crawler does.
/// </summary>
public interface IPageDryRunner
{
    /// <summary>
    ///     Run the configured crawl in dry-run mode. Returns a <see cref="DryRunReport" />
    ///     describing what a full scrape would produce.
    /// </summary>
    Task<DryRunReport> DryRunAsync(ScrapeJob job, CancellationToken ct = default);
}
```

- [ ] **Step 2: Write the failing test**

Write `DocRAG.Tests/Crawling/PageCrawlerDryRunnerTests.cs` with this exact content:

```csharp
// // PageCrawlerDryRunnerTests.cs
// // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// // Use subject to the MIT License.

#region Usings

using DocRAG.Core.Interfaces;
using DocRAG.Ingestion.Crawling;

#endregion

namespace DocRAG.Tests.Crawling;

public sealed class PageCrawlerDryRunnerTests
{
    [Fact]
    public void PageCrawlerImplementsIPageDryRunner()
    {
        Assert.True(typeof(IPageDryRunner).IsAssignableFrom(typeof(PageCrawler)));
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test E:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --filter FullyQualifiedName~PageCrawlerDryRunnerTests`
Expected: FAIL — `PageCrawler` does not implement `IPageDryRunner`.

- [ ] **Step 4: Add the interface to `PageCrawler`'s class declaration**

In `DocRAG.Ingestion/Crawling/PageCrawler.cs`, change the class declaration (modified in Task 6) from:

```csharp
public class PageCrawler : ISourceCrawler
```

to:

```csharp
public class PageCrawler : ISourceCrawler, IPageDryRunner
```

The existing `DryRunAsync(ScrapeJob, CancellationToken)` method on `PageCrawler` already matches the interface signature exactly — no method changes needed.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test E:/GitHub/DocRAG/DocRAG.Tests/DocRAG.Tests.csproj --filter FullyQualifiedName~PageCrawlerDryRunnerTests`
Expected: PASS — 1/1 tests passing.

- [ ] **Step 6: Update `DocRAG.Mcp/Tools/IngestionTools.cs` to depend on the interface**

In `DocRAG.Mcp/Tools/IngestionTools.cs`:

a) Change the parameter on line 35 from:

```csharp
public static async Task<string> DryRunScrape(PageCrawler crawler,
```

to:

```csharp
public static async Task<string> DryRunScrape(IPageDryRunner dryRunner,
```

b) On line 73, change:

```csharp
var report = await crawler.DryRunAsync(job, ct);
```

to:

```csharp
var report = await dryRunner.DryRunAsync(job, ct);
```

c) Update the `using` directives at the top of the file: ensure `using DocRAG.Core.Interfaces;` is present. After this change, `using DocRAG.Ingestion.Crawling;` may no longer be needed in this file (unless other tool methods reference symbols from that namespace). Read the full file first; if `PageCrawler`, `GitHubRepoScraper`, or `SourceCrawlerRegistry` are no longer referenced after this edit, remove the `using DocRAG.Ingestion.Crawling;` line. (Task 11 sweeps any stragglers.)

- [ ] **Step 7: Register `PageCrawler` as `IPageDryRunner` in DI**

In `DocRAG.Mcp/Program.cs`, locate the `ISourceCrawler` registration block (modified in Tasks 8 and 9). Add an `IPageDryRunner` registration immediately after the `ISourceCrawlerRegistry` registration:

```csharp
builder.Services.AddSingleton<ISourceCrawlerRegistry, SourceCrawlerRegistry>();

// Register PageCrawler as IPageDryRunner so the MCP dryrun_scrape tool
// can depend on the abstraction. Forwarding to the same singleton instance.
builder.Services.AddSingleton<IPageDryRunner>(sp => sp.GetRequiredService<PageCrawler>());
```

The forwarding registration ensures the same `PageCrawler` instance answers `PageCrawler`, `ISourceCrawler`, and `IPageDryRunner`.

- [ ] **Step 8: Build the full solution**

Run: `dotnet build E:/GitHub/DocRAG/DocRAG.slnx`
Expected: Build succeeded, 0 errors, 0 warnings.

- [ ] **Step 9: Run the full unit test suite**

Run: `dotnet test E:/GitHub/DocRAG/DocRAG.slnx --filter "Category!=Integration"`
Expected: All unit tests pass.

- [ ] **Step 10: Commit**

```bash
git -C E:/GitHub/DocRAG add DocRAG.Core/Interfaces/IPageDryRunner.cs DocRAG.Ingestion/Crawling/PageCrawler.cs DocRAG.Tests/Crawling/PageCrawlerDryRunnerTests.cs DocRAG.Mcp/Tools/IngestionTools.cs DocRAG.Mcp/Program.cs
git -C E:/GitHub/DocRAG commit -F .git-commit-msg.txt
```

Commit message:

```
Extract IPageDryRunner from PageCrawler

Dry-run is a separate capability from crawl-stage entry, and the MCP
dryrun_scrape tool now depends on IPageDryRunner instead of the concrete
PageCrawler. Future crawlers (FileSystem, Video) can opt into dry-run
support by implementing this interface, without touching the tool.

PageCrawler now implements both ISourceCrawler and IPageDryRunner;
the existing DryRunAsync method already matches the interface signature
so no behavior changes.
```

---

### Task 11: Sweep stale `DocRAG.Ingestion.Crawling` imports

Cosmetic cleanup. After Tasks 7 and 10, `IngestionOrchestrator.cs` and `IngestionTools.cs` no longer reference symbols from the `DocRAG.Ingestion.Crawling` namespace; their `using` directives for that namespace are dead.

**Files:**
- Modify: `DocRAG.Ingestion/IngestionOrchestrator.cs`
- Modify: `DocRAG.Mcp/Tools/IngestionTools.cs` (only if not already cleaned up in Task 10 Step 6)

- [ ] **Step 1: Verify which files have a stale crawling import**

Run a Grep for the import across the affected projects:

Search pattern: `using DocRAG\.Ingestion\.Crawling;`
Across paths: `E:/GitHub/DocRAG/DocRAG.Ingestion/IngestionOrchestrator.cs`, `E:/GitHub/DocRAG/DocRAG.Mcp/Tools/`

Expected: matches in `IngestionOrchestrator.cs` and possibly `IngestionTools.cs`. `Program.cs` should NOT be on this list — it still registers concrete `PageCrawler` and `GitHubRepoScraper` types and needs the import.

For each matching file, before removing the import, confirm the file references no symbols from that namespace (i.e., no occurrences of `PageCrawler`, `GitHubRepoScraper`, `SourceCrawlerRegistry` outside the `using` line itself). If references remain, **leave the import alone**.

- [ ] **Step 2: Remove the stale import from `IngestionOrchestrator.cs`**

In `DocRAG.Ingestion/IngestionOrchestrator.cs`, find the usings block and delete the line:

```csharp
using DocRAG.Ingestion.Crawling;
```

Do not delete `using DocRAG.Ingestion.Chunking;` or `using DocRAG.Ingestion.Classification;` — those namespaces are still referenced (`CategoryAwareChunker`, `LlmClassifier`).

- [ ] **Step 3: Remove the stale import from `IngestionTools.cs` (if not already done in Task 10)**

If Task 10 Step 6 left `using DocRAG.Ingestion.Crawling;` in place, remove it now in `DocRAG.Mcp/Tools/IngestionTools.cs`. Verify no symbols from the namespace remain in the file before deleting.

- [ ] **Step 4: Build the full solution**

Run: `dotnet build E:/GitHub/DocRAG/DocRAG.slnx -p:TreatWarningsAsErrors=true`
Expected: Build succeeded, 0 errors, 0 warnings. (Treating warnings as errors here catches an "unused using" warning if you missed something — it would fail the build rather than slipping through.)

- [ ] **Step 5: Run the full unit test suite**

Run: `dotnet test E:/GitHub/DocRAG/DocRAG.slnx --filter "Category!=Integration"`
Expected: All unit tests pass — the import sweep is purely cosmetic and shouldn't affect test outcomes.

- [ ] **Step 6: Commit**

```bash
git -C E:/GitHub/DocRAG add DocRAG.Ingestion/IngestionOrchestrator.cs DocRAG.Mcp/Tools/IngestionTools.cs
git -C E:/GitHub/DocRAG commit -F .git-commit-msg.txt
```

Commit message:

```
Drop stale DocRAG.Ingestion.Crawling imports

After the registry refactor, IngestionOrchestrator and IngestionTools no
longer reference symbols from the crawling namespace. Removed the dead
using directives. Program.cs still imports it (registers concrete types).
```

---

### Task 12: End-to-end verification — coding product still works

**Files:**
- (No code changes; verification only.)

- [ ] **Step 1: Full solution build with treat-warnings-as-errors**

Run: `dotnet build E:/GitHub/DocRAG/DocRAG.slnx -p:TreatWarningsAsErrors=true`
Expected: Build succeeded, 0 errors, 0 warnings.

- [ ] **Step 2: Full unit test pass**

Run: `dotnet test E:/GitHub/DocRAG/DocRAG.slnx --filter "Category!=Integration"`
Expected: All unit tests pass (existing + 16 new tests added by this plan: 7 registry, 2 PageCrawler.Kind, 1 PageCrawler.IPageDryRunner, 4 GitHubRepoScraper, 2 orchestrator dispatch).

- [ ] **Step 3: Smoke-test the MCP service against real scrapes**

This validates the coding-product end-to-end path through the new abstraction, exercising both `SourceKind.Web` (PageCrawler) and `SourceKind.GitHub` (GitHubRepoScraper as top-level entry).

a) Ensure MongoDB and Ollama are running locally (defaults in `appsettings.Development.json`).

b) Run the service:

```bash
dotnet run --project E:/GitHub/DocRAG/DocRAG.Mcp/DocRAG.Mcp.csproj
```

Expected: server starts on `http://localhost:6100`, no errors in logs about missing `ISourceCrawler`, `ISourceCrawlerRegistry`, or `IPageDryRunner`.

c) From a separate terminal, verify health:

```bash
curl -sS http://localhost:6100/health
```

Expected: HTTP 200 with `{"Status":"Healthy", ...}`.

d) Use any MCP client (Claude Code with the `.mcp.json` registered) to:

1. Call `dryrun_scrape` against a small site like `https://playwright.dev/docs/intro`. This exercises the `IPageDryRunner` path. Confirm the dry-run completes and reports page counts.
2. Call `scrape_docs` against the same URL. This exercises the registry-dispatch path with `SourceKind.Web`. Confirm pages land in MongoDB and the service log includes `Dispatching crawl for ... to PageCrawler (SourceKind=Web)`.
3. Call `scrape_docs` against a small GitHub repo (e.g., `https://github.com/anthropics/courses`) with `SourceKind.GitHub` (if the MCP tool exposes the kind parameter — otherwise the URL pattern still routes via PageCrawler's internal delegation, which is also a valid coverage path). Confirm files land in MongoDB and the dispatch log line names the crawler that ran.

If any step fails, **do not commit anything new** — diagnose the regression. The dispatch log line is the new instrumentation introduced by this refactor and is the easiest way to confirm the new path is live.

- [ ] **Step 4: Stop the dev service**

Ctrl-C the `dotnet run` terminal.

- [ ] **Step 5: No commit needed if everything passed**

If verification produced no code changes, this task closes with no commit. If you had to fix a regression, commit the fix with a message describing what broke and why.

---

## Self-Review Checklist (run after writing the plan)

**Spec coverage:**
- [x] `SourceKind` enum → Task 1
- [x] `ISourceCrawler` interface defined → Task 2
- [x] `ISourceCrawlerRegistry` defined → Task 3
- [x] `SourceCrawlerRegistry` implemented with TDD → Task 4
- [x] `ScrapeJob.SourceKind` default Web → Task 5
- [x] `PageCrawler` implements `ISourceCrawler` → Task 6
- [x] `IngestionOrchestrator` dispatches via registry → Task 7
- [x] DI wiring updated → Task 8
- [x] `GitHubRepoScraper` promoted to top-level `ISourceCrawler` (Kind=GitHub) → Task 9
- [x] `IPageDryRunner` extracted from `PageCrawler` and consumed by `IngestionTools` → Task 10
- [x] Stale `DocRAG.Ingestion.Crawling` imports swept → Task 11
- [x] Backward compatibility for coding product → Task 5 (default), Task 12 (verification)
- [x] End-to-end smoke test exercises Web, GitHub, and dry-run paths → Task 12

**Placeholder scan:** No "TBD" / "TODO" / "implement later" / "similar to Task N" / "appropriate error handling" appear in any task. Every task has full code.

**Type consistency:**
- `ISourceCrawler.CrawlAsync` parameters in Task 2 match `PageCrawler.CrawlAsync` exactly (verified against existing source) and the new `GitHubRepoScraper.CrawlAsync` defined in Task 9.
- `SourceCrawlerRegistry` constructor takes `IEnumerable<ISourceCrawler>` in Tasks 3, 4, 7.
- `IngestionOrchestrator` constructor parameter named `crawlers` (plural) and field `mCrawlers` consistent across Tasks 7, 8.
- `Get(SourceKind)` and `TryGet(SourceKind, out ISourceCrawler?)` method shapes match between interface (Task 3), implementation (Task 4), and consumer (Task 7).
- `SourceKind.Web` referenced in Tasks 1, 5, 6, 7, 8, 12 — single canonical value.
- `SourceKind.GitHub` referenced in Tasks 1, 9 — single canonical value.
- `IPageDryRunner.DryRunAsync(ScrapeJob, CancellationToken)` shape in Task 10 matches the existing `PageCrawler.DryRunAsync` method signature exactly.

---

## Out of scope (explicitly deferred)

- **`LibrarySurveyor` + dynamic taxonomy.** Next plan. Required before Medical/Engineering products produce useful classification, but orthogonal to the dispatch abstraction.
- **`FileSystemCrawler`.** Plan after `LibrarySurveyor`. First real consumer of a non-Web/non-GitHub `ISourceCrawler`.
- **Threading `onPageFetched`/`onQueued` progress callbacks through `GitHubRepoScraper.ScrapeRepositoryAsync`.** Task 9 documents the limitation; downstream stages (chunks generated/embedded/completed) report normally so completion progress is observable. A small follow-up can enrich `ScrapeRepositoryAsync`'s signature with an optional callback if the dark `PagesFetched` counter becomes a real UX problem.
